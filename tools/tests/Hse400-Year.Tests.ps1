# tools/tests/Hse400-Year.Tests.ps1
# Сторож приймання HSE400 (крок 4): зіставлення аркуша звіту HSE400.Year з моделлю і звірка розрахункових комірок.
# Дані - синтетичні (значень замовника в git немає). Pester 5 (CI, pwsh) і 3.4 (локально); без кутових дужок у назвах.
Describe 'HSE400 Year acceptance' {
    BeforeAll {
        $root = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $root 'tools/hse400/Hse400Year.Lib.ps1')

        function New-TestModel {
            $t1 = [pscustomobject]@{ code = 'FG_A'; rows = @(
                    [pscustomobject]@{ key = 'Methane'; kind = 'Input'; en = 'Methane (CH4), vol%'; ru = 'Метан (CH4), % об.' }
                    [pscustomobject]@{ key = 'F_H2S'; kind = 'Calc'; en = 'Hydrogen sulfide (H2S), vol%'; ru = 'Сероводород (H2S), % об.' }
                    [pscustomobject]@{ key = 'F_Total'; kind = 'Calc'; en = 'Total, vol% (must equal 100)'; ru = 'Сумма' }) }
            $t2 = [pscustomobject]@{ code = 'FG_B'; rows = @(
                    [pscustomobject]@{ key = 'IC4_1'; kind = 'Input'; en = 'Isobutane (i-C4H10), vol%'; ru = 'Изобутан' }
                    [pscustomobject]@{ key = 'F_Density20'; kind = 'Calc'; en = 'Density, kg/Sm3 (20 °C, 1 atm)'; ru = 'Плотность' }
                    [pscustomobject]@{ key = 'F_GHG1'; kind = 'Calc'; en = 'GHG step 1'; ru = 'шаг 1' }
                    [pscustomobject]@{ key = 'F_GHG2'; kind = 'Calc'; en = 'GHG step 2'; ru = 'шаг 2' }) }
            return [pscustomobject]@{ tables = @($t1, $t2) }
        }
        function New-YRow([int]$n, [string]$label, $values) { return [pscustomobject]@{ row = $n; label = $label; values = $values } }
        function New-TestRows {
            $nb = [string][char]0x00A0
            return @(
                (New-YRow 1 'Data view: 2026 year' @()), (New-YRow 3 'Gas composition' @('January', 'February')),
                (New-YRow 5 'Type of gas - Тип газа' @()), (New-YRow 6 'Compositional Makat gas analysis, vol% - Состав' @($null, $null)),
                (New-YRow 7 'Methane (CH4), vol% - Метан (CH4), % об.' @(90.5, 91.0)),
                (New-YRow 8 "Hydrogen Sulfide (H2S), vol%$nb - Сероводород" @(0.1, 0.2)),
                (New-YRow 9 'Compositional Makat gas analysis, wt% - Состав' @($null, $null)),
                (New-YRow 10 'Сумма должна быть равна 100' @(100, $null)),
                (New-YRow 11 '' @()),
                (New-YRow 12 'Type of gas - Тип газа' @()), (New-YRow 13 'Compositional Fuel gas analysis - Состав' @()),
                (New-YRow 14 'IC4_18*_Isobutane (i-C4H10), vol% - Изобутан' @(1.5, 1.6)),
                (New-YRow 15 'Density, kg/Sm3 (20 dgr., 1atm) - Плотность' @(0.7, 0.71)))
        }
    }

    It 'блоки: заголовки підблоків і порожні рядки відкинуто' {
        $b = Split-Hse400YearBlocks (New-TestRows)
        if ($b.Count -ne 2) { throw "блоків $($b.Count)" }
        if (@($b[0].Rows).Count -ne 3) { throw "рядків першого блоку $(@($b[0].Rows).Count)" }
        if ($b[0].Rows[2].Row -ne 10) { throw "третій рядок $($b[0].Rows[2].Row)" }
    }
    It 'позиційне зіставлення без проміжних F_GHG1 і F_GHG2' {
        $j = Join-Hse400YearRows -Model (New-TestModel) -Blocks (Split-Hse400YearBlocks (New-TestRows))
        if (@($j.Problems).Count) { throw ($j.Problems -join '; ') }
        $keys = @($j.Items | ForEach-Object { "$($_.Table)/$($_.RowKey)/$($_.SourceRow)" }) -join ','
        if ($keys -ne 'FG_A/Methane/7,FG_A/F_H2S/8,FG_A/F_Total/10,FG_B/IC4_1/14,FG_B/F_Density20/15') { throw $keys }
    }
    It 'зсув рядків ловиться за формулою в дужках' {
        $rows = New-TestRows
        $rows[4].label = 'Ethane (C2H6), vol% - Этан'
        $j = Join-Hse400YearRows -Model (New-TestModel) -Blocks (Split-Hse400YearBlocks $rows)
        if (-not (@($j.Problems) -match 'FG_A/Methane')) { throw "немає помилки зсуву: $($j.Problems -join '; ')" }
    }
    It 'інший порядок рядків із формулою у звіті - пари за підписом, n-префікс не заважає' {
        $model = [pscustomobject]@{ tables = @([pscustomobject]@{ code = 'FG_I'; rows = @(
                        [pscustomobject]@{ key = 'F_nPMercaptan_mass'; kind = 'Calc'; en = 'n-Propyl mercaptan (n-C3H8S), wt%'; ru = 'x' }
                        [pscustomobject]@{ key = 'F_Methane_mass'; kind = 'Calc'; en = 'Methane (CH4), wt%'; ru = 'x' }
                        [pscustomobject]@{ key = 'F_Methane'; kind = 'Calc'; en = 'Methane (CH4), vol%'; ru = 'x' }
                        [pscustomobject]@{ key = 'F_Total'; kind = 'Calc'; en = 'Total, wt%'; ru = 'x' }) }) }
        $rows = @((New-YRow 1 'Type of gas' @()), (New-YRow 2 'Block' @()),
            (New-YRow 3 'Methane (CH4), vol% - x' @(1.0)), (New-YRow 4 'Methane (CH4), wt% - x' @(2.0)),
            (New-YRow 5 'Propyl mercaptan (C3H8S), wt% - x' @(3.0)), (New-YRow 6 'Сумма должна быть равна 100' @(100)))
        $j = Join-Hse400YearRows -Model $model -Blocks (Split-Hse400YearBlocks $rows)
        if (@($j.Problems).Count) { throw ($j.Problems -join '; ') }
        $keys = @($j.Items | ForEach-Object { "$($_.RowKey)/$($_.SourceRow)" }) -join ','
        if ($keys -ne 'F_nPMercaptan_mass/5,F_Methane_mass/4,F_Methane/3,F_Total/6') { throw $keys }
    }
    It 'формули без цифр і кириличні двійники; температура не стає в пару з сумою' {
        if ((Get-Hse400YearFormulaToken 'Carbonyl Sulfide (COS), vol% - x') -ne 'cos') { throw 'COS' }
        if ((Get-Hse400YearFormulaToken 'Helium (He), wt% - x') -ne 'he') { throw 'He' }
        $tol = 'Toluene (' + [char]0x0421 + '7' + [char]0x041D + '8), vol% - x'
        if ((Get-Hse400YearFormulaToken $tol) -ne 'c7h8') { throw "Toluene $(Get-Hse400YearFormulaToken $tol)" }
        if ((Get-Hse400YearFormulaToken 'Density, kg/Sm3 (20 dgr., 1atm) - x') -ne '') { throw 'density' }
        if ((Get-Hse400YearFormulaToken 'Vwdg (для влажного) - x') -ne '') { throw 'cyrillic words' }
        $model = [pscustomobject]@{ tables = @([pscustomobject]@{ code = 'FG_H'; rows = @(
                        [pscustomobject]@{ key = 'Temperature'; kind = 'Input'; en = 'Initial temperature To, C'; ru = 'x' }
                        [pscustomobject]@{ key = 'F_Total'; kind = 'Calc'; en = 'Total, vol% (must equal 100)'; ru = 'x' }
                        [pscustomobject]@{ key = 'F_MolecularWeight'; kind = 'Calc'; en = 'Molecular weight'; ru = 'x' }) }) }
        $rows = @((New-YRow 1 'Type of gas' @()), (New-YRow 2 'Block' @()),
            (New-YRow 3 'Сумма должна быть равна 100' @(100)), (New-YRow 4 'Initial temperature of flared mixture To' @(20)),
            (New-YRow 5 'Molecular weight - x' @(17)))
        $j = Join-Hse400YearRows -Model $model -Blocks (Split-Hse400YearBlocks $rows)
        $keys = @($j.Items | ForEach-Object { "$($_.RowKey)/$($_.SourceRow)" }) -join ','
        if ($keys -ne 'Temperature/4,F_Total/3,F_MolecularWeight/5') { throw $keys }
    }
    It 'різна кількість рядків - помилка, а не мовчазний зсув' {
        $rows = @(New-TestRows | Where-Object { $_.row -ne 8 })
        $j = Join-Hse400YearRows -Model (New-TestModel) -Blocks (Split-Hse400YearBlocks $rows)
        if (-not (@($j.Problems) -match 'FG_A: рядків')) { throw "немає помилки кількості: $($j.Problems -join '; ')" }
    }
    It 'допуск: відносна 1e-9, абсолютна 1e-12 біля нуля, порожнє проти числа - розбіжність' {
        if (-not (Test-Hse400YearValueMatch 100.0 100.00000001)) { throw 'rel 1e-10 має збігатися' }
        if (Test-Hse400YearValueMatch 100.0 100.000001) { throw 'rel 1e-8 не має збігатися' }
        if (-not (Test-Hse400YearValueMatch 0.0 5e-13)) { throw 'abs 5e-13 має збігатися' }
        if (Test-Hse400YearValueMatch 1.0 $null) { throw 'число проти порожнього не збіг' }
        if (-not (Test-Hse400YearValueMatch $null $null)) { throw 'обидва порожні - збіг' }
    }
    It 'ввід документа: лише Input, порожні місяці пропущено' {
        $j = Join-Hse400YearRows -Model (New-TestModel) -Blocks (Split-Hse400YearBlocks (New-TestRows))
        $w = Get-Hse400YearInputRows -Items $j.Items
        if (@($w.Keys) -join ',' -ne 'FG_A,FG_B') { throw "таблиці $(@($w.Keys) -join ',')" }
        $m = @($w['FG_A'])
        if ($m.Count -ne 1 -or $m[0].rowKey -ne 'Methane' -or @($m[0].cells).Count -ne 2) { throw 'ввід FG_A' }
        if ($m[0].cells[1].columnCode -ne 'M02' -or $m[0].cells[1].value -ne [decimal]91.0) { throw 'місяць M02' }
    }
    It 'звірка: кожна розбіжна комірка названа таблицею, рядком і місяцем' {
        $j = Join-Hse400YearRows -Model (New-TestModel) -Blocks (Split-Hse400YearBlocks (New-TestRows))
        $actual = @{
            FG_A = @{ F_H2S = @{ M01 = 0.1; M02 = 0.25 }; F_Total = @{ M01 = 100.0 } }
            FG_B = @{ F_Density20 = @{ M01 = 0.7 } }
        }
        $c = Compare-Hse400YearResults -Items $j.Items -Actual $actual -Months 2
        if ($c.Compared -ne 5 -or $c.Matched -ne 3) { throw "compared $($c.Compared) matched $($c.Matched)" }
        $d = @($c.Diffs | ForEach-Object { "$($_.Table)/$($_.RowKey)/$($_.Month)" }) -join ','
        if ($d -ne 'FG_A/F_H2S/M02,FG_B/F_Density20/M02') { throw $d }
    }
    It 'зріз документа: числа місячних колонок, решта пропущена' {
        $slice = [pscustomobject]@{ rows = @([pscustomobject]@{ rowKey = 'F_H2S'; cells = [pscustomobject]@{ M01 = 0.5; M02 = $null; Anchor = 'x' } }) }
        $s = ConvertFrom-Hse400YearSlice $slice
        if ($s['F_H2S']['M01'] -ne 0.5 -or $s['F_H2S'].ContainsKey('M02') -or $s['F_H2S'].ContainsKey('Anchor')) { throw 'зріз' }
    }
}
