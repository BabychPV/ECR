<#
    HSE400 крок 4 (приймання): зіставлення аркуша «GasComposition <рік>» звіту HSE400.Year.xlsx (вивантаження
    hse400_year_dump.py) з моделлю шаблону HSE400 і звірка розрахункових комірок документа зі звітом.
    Чиста логіка без мережі; тести - tools/tests/Hse400-Year.Tests.ps1.

    Будова аркуша (звіт прода, 11.11): колонка A - назва рядка «en - ru», B.. - місяці з січня (у звіті 11, без грудня);
    блок газу починається рядком «Type of gas - Тип газа», далі рядок-назва блоку, далі рядки даних; рядки-заголовки
    підблоків («Compositional … analysis, wt% …») чисел не мають. Блоки йдуть у порядку таблиць моделі.

    Зіставлення - за ПІДПИСОМ (Get-Hse400YearSignature: одиниця vol%/wt% + хімічна формула в дужках) зі збереженням
    порядку всередині підпису; рядки без формули (суми, густини, теплота, V…) - по порядку між собою. Проміжних рядків
    моделі (F_GHG1/F_GHG2 - кроки коефіцієнта викидів) у звіті немає - їх не зіставляють. Назви в моделі вичищені
    («20 °C» замість «20 dgr.»), тож повний збіг назв не вимагається. Будь-який рядок без пари - помилка зіставлення,
    а не розбіжність значень.
#>

$script:Hse400YearIntermediateRows = @('F_GHG1', 'F_GHG2')
$script:Hse400YearBlockMarker = 'Type of gas'

function ConvertTo-Hse400YearLabel {
    <# Назва для порівняння: NBSP -> пробіл, без службового префікса «IC4_18*_», один пробіл, нижній регістр. #>
    param([string]$Text)
    if ($null -eq $Text) { return '' }
    $s = $Text.Replace([string][char]0x00A0, ' ').Trim()
    $s = $s -replace '^[A-Za-z0-9]+_\d+\*_', ''
    $s = $s -replace '\s+', ' '
    return $s.ToLowerInvariant()
}

function Get-Hse400YearFormulaToken {
    <# Перша хімічна формула в дужках англійської частини назви («(CH4)», «(i-C4H10)»); немає - ''. Кирилична «С» -> латинська. #>
    param([string]$Label)
    $en = (ConvertTo-Hse400YearLabel $Label).Split(@(' - '), 2, [StringSplitOptions]::None)[0]
    # Кириличні двійники латинських літер у формулах звіту («Толуол (С7Н8)»): с н о а р е х -> c h o a p e x.
    $twins = @{ 0x0441 = 'c'; 0x043D = 'h'; 0x043E = 'o'; 0x0430 = 'a'; 0x0440 = 'p'; 0x0435 = 'e'; 0x0445 = 'x' }
    foreach ($k in $twins.Keys) { $en = $en.Replace([string][char]$k, $twins[$k]) }
    # Формула - лише латиниця/цифри/σ/дефіс без пробілів, ≥ 2 знаки: (CH4), (COS), (He), (RSH), (i-C4H10), (ΣC6H14).
    $m = [regex]::Match($en, '\(([a-z0-9σ\-]{2,})\)')
    if ($m.Success) { return $m.Groups[1].Value }
    return ''
}

function Get-Hse400YearSignature {
    <#
    Підпис рядка для зіставлення: «wt:формула» / «vol:формула» / «x:формула» (одиниця з англійської частини назви),
    провідне «n-» формули відкинуто (у моделі «n-propyl (n-C3H8S)», у звіті «propyl (C3H8S)»); без формули - «x».
    #>
    param([string]$Label)
    $tok = Get-Hse400YearFormulaToken $Label
    if ($tok -eq '') {
        # Температура вводу стоїть у моделі ДО рядка суми, у звіті - ПІСЛЯ (FG_HMB/LPG_HMB): окремий підпис.
        if ((ConvertTo-Hse400YearLabel $Label) -match 'temperature') { return 'k:temp' }
        return 'x'
    }
    $tok = $tok -replace '^n-', ''
    $en = (ConvertTo-Hse400YearLabel $Label).Split(@(' - '), 2, [StringSplitOptions]::None)[0]
    $unit = if ($en -match 'wt%') { 'wt' } elseif ($en -match 'vol%') { 'vol' } else { 'x' }
    return "${unit}:$tok"
}

function Split-Hse400YearBlocks {
    <#
    Рядки вивантаження -> блоки газу: @{ Name; Rows = @(@{ Row; Label; Values }) }. Порожні назви й заголовки підблоків
    (без жодного числа, «analysis» у назві) відкидаються.
    #>
    param([Parameter(Mandatory)]$Rows)
    $blocks = New-Object System.Collections.Generic.List[object]
    $cur = $null
    foreach ($r in @($Rows)) {
        if ($null -eq $r) { continue }
        $label = [string]$r.label
        $t = $label.Trim()
        if ($t.StartsWith($script:Hse400YearBlockMarker, [StringComparison]::OrdinalIgnoreCase)) {
            $cur = [pscustomobject]@{ Name = $null; Rows = (New-Object System.Collections.Generic.List[object]) }
            $blocks.Add($cur); continue
        }
        if ($null -eq $cur) { continue }
        if ($null -eq $cur.Name) { $cur.Name = $t; continue }
        if ($t -eq '') { continue }
        $vals = @($r.values)
        $hasNumber = @($vals | Where-Object { $null -ne $_ }).Count -gt 0
        if (-not $hasNumber -and $t -match '(?i)analysis') { continue }
        $cur.Rows.Add([pscustomobject]@{ Row = [int]$r.row; Label = $label; Values = $vals })
    }
    # Rows - масив, не List: @() над List[object] у властивості PSCustomObject у PS 5.1 дає «Argument types do not match».
    $out = foreach ($b in $blocks) { [pscustomobject]@{ Name = $b.Name; Rows = $b.Rows.ToArray() } }
    return , @($out)
}

function Join-Hse400YearRows {
    <#
    Позиційне зіставлення блоків звіту з таблицями моделі. Повертає @{ Items; Problems }:
    Items - @{ Table; RowKey; Kind; SourceRow; Values }, Problems - помилки зіставлення (порожньо - зіставлено).
    #>
    param([Parameter(Mandatory)]$Model, [Parameter(Mandatory)]$Blocks)
    $items = New-Object System.Collections.Generic.List[object]
    $problems = New-Object System.Collections.Generic.List[string]
    $tables = @($Model.tables)
    $blocks = @($Blocks)
    if ($blocks.Count -ne $tables.Count) { $problems.Add("блоків у звіті $($blocks.Count), таблиць у моделі $($tables.Count)") }
    for ($i = 0; $i -lt [Math]::Min($blocks.Count, $tables.Count); $i++) {
        $t = $tables[$i]; $b = $blocks[$i]
        $modelRows = @($t.rows | Where-Object { $script:Hse400YearIntermediateRows -notcontains [string]$_.key })
        $src = @($b.Rows)
        if ($modelRows.Count -ne $src.Count) {
            $problems.Add("$($t.code): рядків у моделі (без проміжних) $($modelRows.Count), у звіті $($src.Count)")
            continue
        }
        # Підпис рядка: «одиниця:формула», без формули - «x». Пара - k-й рядок моделі й k-й рядок звіту з тим самим
        # підписом (порядок усередині підпису зберігається; між підписами - ні: у FG_/LPG_Internal підблок wt% моделі
        # має сірчані рядки в іншому місці, ніж звіт).
        $queues = @{}
        foreach ($sr in $src) {
            $sig = Get-Hse400YearSignature $sr.Label
            if (-not $queues.ContainsKey($sig)) { $queues[$sig] = New-Object System.Collections.Generic.Queue[object] }
            $queues[$sig].Enqueue($sr)
        }
        foreach ($mr in $modelRows) {
            $sig = Get-Hse400YearSignature ("{0} - {1}" -f $mr.en, $mr.ru)
            if (-not $queues.ContainsKey($sig) -or $queues[$sig].Count -eq 0) {
                $problems.Add("$($t.code)/$($mr.key): у звіті немає рядка з підписом $sig")
                continue
            }
            $sr = $queues[$sig].Dequeue()
            $items.Add([pscustomobject]@{ Table = [string]$t.code; RowKey = [string]$mr.key; Kind = [string]$mr.kind; SourceRow = $sr.Row; Values = @($sr.Values) })
        }
        foreach ($q in $queues.GetEnumerator()) {
            foreach ($left in $q.Value) { $problems.Add("$($t.code): рядок звіту $($left.Row) (підпис $($q.Key)) без пари в моделі") }
        }
    }
    return [pscustomobject]@{ Items = $items.ToArray(); Problems = $problems.ToArray() }
}

function Test-Hse400YearValueMatch {
    <#
    Правило приймання (план HSE400 (d)): відносна похибка ≤ RelTol, або абсолютна ≤ AbsTol для значень ~0.
    Обидва порожні - збіг; одне порожнє - розбіжність.
    #>
    param($Expected, $Actual, [double]$RelTol = 1e-9, [double]$AbsTol = 1e-12)
    if ($null -eq $Expected -and $null -eq $Actual) { return $true }
    if ($null -eq $Expected -or $null -eq $Actual) { return $false }
    $e = [double]$Expected; $a = [double]$Actual
    $d = [Math]::Abs($e - $a)
    if ($d -le $AbsTol) { return $true }
    $scale = [Math]::Max([Math]::Abs($e), [Math]::Abs($a))
    return ($d / $scale) -le $RelTol
}

function Get-Hse400YearInputRows {
    <#
    Ввід документа з рядків звіту: таблиця -> рядки PATCH /documents/{id}/cells (rowKey, cells[columnCode M01.., value]).
    Лише рядки Input і лише місяці 1..Months; порожня клітинка звіту не пишеться.
    #>
    param([Parameter(Mandatory)]$Items, [int]$Months = 12)
    $result = [ordered]@{}
    foreach ($g in @(@($Items) | Where-Object { $_.Kind -eq 'Input' } | Group-Object Table)) {
        $rows = New-Object System.Collections.Generic.List[object]
        foreach ($it in $g.Group) {
            $cells = New-Object System.Collections.Generic.List[object]
            $vals = @($it.Values)
            for ($m = 1; $m -le [Math]::Min($Months, $vals.Count); $m++) {
                if ($null -eq $vals[$m - 1]) { continue }
                $cells.Add(@{ columnCode = ('M{0:D2}' -f $m); isEmpty = $false; value = [decimal][double]$vals[$m - 1] })
            }
            if ($cells.Count) { $rows.Add(@{ rowKey = $it.RowKey; cells = $cells.ToArray() }) }
        }
        $result[[string]$g.Name] = $rows.ToArray()
    }
    return $result
}

function Compare-Hse400YearResults {
    <#
    Звірка розрахункових рядків. -Actual: таблиця -> rowKey -> columnCode -> значення (зріз документа).
    Повертає @{ Compared; Matched; Diffs = @(@{ Table; RowKey; SourceRow; Month; Expected; Actual; RelErr }) } по місяцях
    1..Months; комірка, де звіт порожній, а документ теж порожній, у Compared не йде.
    #>
    param([Parameter(Mandatory)]$Items, [Parameter(Mandatory)][hashtable]$Actual, [int]$Months = 11,
        [double]$RelTol = 1e-9, [double]$AbsTol = 1e-12)
    $diffs = New-Object System.Collections.Generic.List[object]
    $compared = 0; $matched = 0
    foreach ($it in @(@($Items) | Where-Object { $_.Kind -eq 'Calc' })) {
        $vals = @($it.Values)
        for ($m = 1; $m -le $Months; $m++) {
            $col = 'M{0:D2}' -f $m
            $exp = if ($m -le $vals.Count) { $vals[$m - 1] } else { $null }
            $act = $null
            if ($Actual.ContainsKey($it.Table) -and $Actual[$it.Table].ContainsKey($it.RowKey) -and $Actual[$it.Table][$it.RowKey].ContainsKey($col)) {
                $act = $Actual[$it.Table][$it.RowKey][$col]
            }
            if ($null -eq $exp -and $null -eq $act) { continue }
            $compared++
            if (Test-Hse400YearValueMatch $exp $act $RelTol $AbsTol) { $matched++; continue }
            $rel = $null
            if ($null -ne $exp -and $null -ne $act) {
                $scale = [Math]::Max([Math]::Abs([double]$exp), [Math]::Abs([double]$act))
                $rel = if ($scale -gt 0) { [Math]::Abs([double]$exp - [double]$act) / $scale } else { 0 }
            }
            $diffs.Add([pscustomobject]@{ Table = $it.Table; RowKey = $it.RowKey; SourceRow = $it.SourceRow; Month = $col
                    Expected = $exp; Actual = $act; RelErr = $rel })
        }
    }
    return [pscustomobject]@{ Compared = $compared; Matched = $matched; Diffs = $diffs.ToArray() }
}

function ConvertFrom-Hse400YearSlice {
    <# Зріз GET /documents/{id}/tables/{inst}?periodKey= -> rowKey -> columnCode -> число (не число й null - пропуск). #>
    param([Parameter(Mandatory)]$Slice)
    $out = @{}
    foreach ($r in @($Slice.rows)) {
        if ($null -eq $r) { continue }
        $cells = @{}
        if ($null -ne $r.cells) {
            foreach ($p in $r.cells.PSObject.Properties) {
                if ($p.Name -notmatch '^M\d{2}$' -or $null -eq $p.Value) { continue }
                $d = 0.0
                if ([double]::TryParse([string]$p.Value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { $cells[$p.Name] = $d }
            }
        }
        $out[[string]$r.rowKey] = $cells
    }
    return $out
}
