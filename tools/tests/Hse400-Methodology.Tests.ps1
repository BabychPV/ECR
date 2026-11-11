# tools/tests/Hse400-Methodology.Tests.ps1
# Сторож договору будівника HSE400 з модулем місячних колонок: імена аргументів і формул, план методології,
# тіла запитів (MonthsInColumns, якір Calculated, рівень Module, правило "{}"), перевірка одиниці 'one' і структури.
# Без мережі й бази. Підготовка - у BeforeAll (CI: Pester 5 на pwsh/Linux; локально й Pester 3.4).
Describe 'HSE400 methodology contract' {
    BeforeAll {
        $root = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $root 'tools/hse400/Hse400Model.Lib.ps1')
        . (Join-Path $root 'tools/hse400/Hse400Methodology.Lib.ps1')

        function Get-Hse400MTestModel {
            $p = (Resolve-Path (Join-Path $PSScriptRoot '../hse400/hse400-model.json')).Path
            return ([IO.File]::ReadAllText($p, [Text.Encoding]::UTF8) | ConvertFrom-Json)
        }

        function Assert-Hse400Throws {
            param([scriptblock]$Action, [string]$Expect)
            $msg = $null
            try { & $Action } catch { $msg = $_.Exception.Message }
            if ($null -eq $msg) { throw "Виняток не кинуто (очікувалося: $Expect)." }
            if ($msg -notlike "*$Expect*") { throw "Виняток не про те: $msg" }
        }

        function New-Hse400TestStructure {
            <# Структура у формі GET /template-versions/{vid}/structure, що відповідає моделі. #>
            param($Model, [string]$Layout = 'MonthsInColumns', [switch]$NoAnchor)
            $id = 1000
            $tables = @()
            foreach ($t in @($Model.tables)) {
                $cols = @()
                for ($i = 1; $i -le 12; $i++) { $id++; $cols += [pscustomobject]@{ id = $id; code = ('M{0:00}' -f $i); dataType = 'Decimal' } }
                if (-not $NoAnchor) { $id++; $cols += [pscustomobject]@{ id = $id; code = (Get-Hse400AnchorColumnCode); dataType = 'Calculated' } }
                $tables += [pscustomobject]@{ code = [string]$t.code; layoutKind = $Layout; columns = $cols }
            }
            return [pscustomobject]@{ sheets = @([pscustomobject]@{ code = [string]$Model.sheet.code; tables = $tables }) }
        }
    }

    # Без кутових дужок у назвах: Pester 5 (CI, pwsh) розгортає <ім'я> як змінну.
    It 'аргумент рядка вводу: GCV_ + простір + таблиця + RowKey' {
        $n = Get-Hse400ArgumentName -Namespace 'HSE400' -Table 'FG_Makat' -RowKey 'Methane'
        if ($n -cne 'GCV_HSE400_FG_Makat_Methane') { throw "ім'я $n" }
        $n = Get-Hse400ArgumentName -Namespace 'HSE400' -Table 'LPG_Internal' -RowKey 'IC4_1'
        if ($n -cne 'GCV_HSE400_LPG_Internal_IC4_1') { throw "ім'я $n" }
    }
    It 'рядок F_ аргументом не буває' {
        Assert-Hse400Throws { Get-Hse400ArgumentName -Namespace 'HSE400' -Table 'FG_Makat' -RowKey 'F_Nitrogen' } 'розрахунковий'
    }
    It 'формула рядка F_X: простір + таблиця + X; рядок вводу - виняток' {
        $c = Get-Hse400FormulaCode -Namespace 'HSE400' -Table 'FG_Makat' -RowKey 'F_Nitrogen'
        if ($c -cne 'HSE400_FG_Makat_Nitrogen') { throw "код $c" }
        Assert-Hse400Throws { Get-Hse400FormulaCode -Namespace 'HSE400' -Table 'FG_Makat' -RowKey 'Nitrogen' } 'F_'
    }
    It 'план чинної моделі: 175 аргументів, 325 цілей, 6 прив''язок на якір, 369 формул, 223 константи, 2 тести - готово' {
        $p = Get-Hse400MethodologyPlan (Read-Hse400Model)
        if ($p.Namespace -cne 'HSE400') { throw "простір $($p.Namespace)" }
        if (@($p.Arguments).Count -ne 175) { throw "аргументів $(@($p.Arguments).Count)" }
        if (@($p.Targets).Count -ne 325) { throw "цілей $(@($p.Targets).Count)" }
        $names = @($p.Arguments | ForEach-Object { $_.Name.ToLowerInvariant() })
        if (@($names | Select-Object -Unique).Count -ne $names.Count) { throw 'повтор імені аргументу' }
        foreach ($a in $p.Arguments) { if ($a.Name -notlike 'GCV_HSE400_*' -or $a.RowKey -like 'F_*') { throw "аргумент $($a.Name)" } }
        if (@($p.Bindings).Count -ne 6) { throw "прив'язок $(@($p.Bindings).Count)" }
        $b = @($p.Bindings | Where-Object { $_.Table -eq 'FG_Makat' })[0]
        if ($b.ColumnCode -cne 'HSE400_Calc' -or $b.OutputCode -cne 'HSE400_FG_Makat_H2S') { throw "прив'язка FG_Makat $($b.ColumnCode)/$($b.OutputCode)" }
        if (@($p.MissingFormulas).Count -ne 0 -or -not $p.Ready) { throw "прогалин $(@($p.MissingFormulas).Count), ready $($p.Ready)" }
        $m = Read-Hse400Model
        if (@($m.methodology.formulas).Count -ne 369 -or @($m.methodology.constants).Count -ne 223 -or @($p.Tests).Count -ne 2) {
            throw "формул $(@($m.methodology.formulas).Count), констант $(@($m.methodology.constants).Count), тестів $(@($p.Tests).Count)"
        }
        # кожне посилання на формулу (!X) і константу (CST.X) має ціль у моделі - інакше чернетка не збереться
        $fcodes = @{}; foreach ($f in @($m.methodology.formulas)) { $fcodes[[string]$f.code] = $true }
        $ccodes = @{}; foreach ($c in @($m.methodology.constants)) { $ccodes[[string]$c.code] = $true }
        foreach ($f in @($m.methodology.formulas)) {
            foreach ($x in [regex]::Matches([string]$f.expression, '!([A-Za-z_][A-Za-z0-9_]*)')) { if (-not $fcodes.ContainsKey($x.Groups[1].Value)) { throw "$($f.code): немає формули $($x.Groups[1].Value)" } }
            foreach ($x in [regex]::Matches([string]$f.expression, 'CST\.([A-Za-z_][A-Za-z0-9_]*)')) { if (-not $ccodes.ContainsKey($x.Groups[1].Value)) { throw "$($f.code): немає константи $($x.Groups[1].Value)" } }
        }
    }
    It 'формули на всі F_ і тест - готово; бракує однієї - не готово й названо яку' {
        $m = Get-Hse400MTestModel
        $p0 = Get-Hse400MethodologyPlan $m
        $fs = @($p0.Targets | ForEach-Object { [pscustomobject]@{ code = $_.FormulaCode; expression = '1' } })
        $m.methodology.formulas = $fs
        Assert-Hse400Model $m
        $p = Get-Hse400MethodologyPlan $m
        if (-not $p.Ready -or @($p.MissingFormulas).Count -ne 0) { throw 'повна модель не готова' }
        $m.methodology.formulas = @($fs | Select-Object -Skip 1)
        $p = Get-Hse400MethodologyPlan $m
        if ($p.Ready -or @($p.MissingFormulas).Count -ne 1 -or $p.MissingFormulas[0].FormulaCode -cne $fs[0].code) { throw 'прогалину не названо' }
    }
    It 'оголошені виходи: вихід кожної прив''язки і кожного золотого тесту, без повторів' {
        # Прив'язка на вихід, якого не оголошує жодна версія, - відмова ECR-CALC-0422 bindingUnknownOutput (стенд 11.11),
        # а опубліковану версію вже не доповнити: виходи оголошуються в чернетці до публікації.
        $p = Get-Hse400MethodologyPlan (Read-Hse400Model)
        $o = @($p.Outputs)
        foreach ($b in @($p.Bindings)) { if ($o -cnotcontains $b.OutputCode) { throw "немає виходу прив'язки $($b.OutputCode)" } }
        if ($o -cnotcontains 'HSE400_FG_Makat_EMercaptan' -or $o -cnotcontains 'HSE400_FG_Makat_H2S') { throw 'немає виходу тесту' }
        if (@($o | Select-Object -Unique).Count -ne $o.Count -or $o.Count -ne 7) { throw "виходів $($o.Count): $($o -join ',')" }
    }
    It 'без тесту не готово навіть із усіма формулами' {
        $m = Get-Hse400MTestModel
        $m.methodology.tests = @()
        if ((Get-Hse400MethodologyPlan $m).Ready) { throw 'готово без тесту' }
    }
    It 'золотий тест: публікація рахує його GenericCalculationModule - аргументи з тесту, очікуване за кодом формули' {
        # Публікація бере одиничний ICalculationModule = Generic (Ecr.Calculations/DependencyInjection.cs): він читає
        # input.Arguments і пише лише оголошені виходи. Адреса рядка F_ і ключ "M01" давали actual = null
        # (стенд 11.11: ECR-CALC-0422 goldenSetDiverged).
        $t = @((Read-Hse400Model).methodology.tests)[0]
        $b = New-Hse400GoldenTestBody -Test $t -DocumentId 77 -TableInstanceId 501 -PeriodKey 202601 -Namespace 'HSE400'
        $in = $b.inputJson | ConvertFrom-Json
        if ($in.documentId -ne 77 -or $in.tableInstanceId -ne 501 -or $in.periodKey.value -ne 202601 -or $in.sourceRowKey -cne 'F_H2S') { throw "вхід $($b.inputJson)" }
        $a = @($in.arguments)
        if ($a.Count -ne 30) { throw "аргументів $($a.Count) (очікувано 15 рядків вводу x 2 форми імені)" }
        $w = @($a | Where-Object { $_.argumentCode -ceq 'GCV.HSE400_FG_Makat_WCHS' })
        $w2 = @($a | Where-Object { $_.argumentCode -ceq 'GCV_HSE400_FG_Makat_WCHS' })
        if ($w.Count -ne 1 -or $w[0].value -ne 20 -or $w2.Count -ne 1 -or $w2[0].value -ne 20) { throw 'аргумент WCHS' }
        if ($b.expectedJson -cne '{"HSE400_FG_Makat_H2S":1.315296778169}') { throw "очікуване $($b.expectedJson)" }
        if ($b.tolerance -isnot [decimal] -or $b.tolerance -ne 0.000000001) { throw "допуск $($b.tolerance)" }
        $o = New-Hse400OutputBody -UnitId 7 -Ordinal 2
        if ($o.unitId -ne 7 -or $o.ordinal -ne 2) { throw 'тіло виходу' }
    }
    It 'золотий ввід: комірки за таблицею без повторів; різні значення однієї комірки - відмова' {
        $tests = @((Read-Hse400Model).methodology.tests)
        $w = Get-Hse400GoldenCellWrites -Tests $tests
        if (@($w.Keys).Count -ne 1 -or -not $w.Contains('FG_Makat')) { throw "таблиці $(@($w.Keys) -join ',')" }
        $rows = @($w['FG_Makat'])
        if ($rows.Count -ne 15) { throw "рядків $($rows.Count)" }
        $r = @($rows | Where-Object { $_.rowKey -ceq 'WCHS' })[0]
        $c = @($r.cells)
        if ($c.Count -ne 1 -or $c[0].columnCode -cne 'M01' -or $c[0].isEmpty -or $c[0].value -ne 20) { throw 'комірка WCHS' }
        foreach ($x in $rows) { if ($x.rowKey -like 'F_*') { throw "запис у розрахунковий рядок $($x.rowKey)" } }
        $a = [pscustomobject]@{ table = 'FG_Makat'; month = 'M01'; inputs = [pscustomobject]@{ WCHS = 20 } }
        $b = [pscustomobject]@{ table = 'FG_Makat'; month = 'M01'; inputs = [pscustomobject]@{ WCHS = 21 } }
        Assert-Hse400Throws { Get-Hse400GoldenCellWrites -Tests @($a, $b) } 'різний ввід'
    }
    It 'тіло PATCH комірок: UserEdit, baseVersion із зрізу, без зрізу - null' {
        $rows = @(@{ rowKey = 'WCHS'; cells = @(@{ columnCode = 'M01'; isEmpty = $false; value = [decimal]20 }) }, @{ rowKey = 'WCMS'; cells = @(@{ columnCode = 'M01'; isEmpty = $false; value = [decimal]16 }) })
        $b = New-Hse400CellsPatchBody -PeriodKey 202601 -TableInstanceId 501 -Rows $rows -RowVersions @{ WCHS = 'AAAB' }
        if ($b.origin -cne 'UserEdit' -or $b.periodKey -ne 202601 -or $b.tableInstanceId -ne 501 -or @($b.rows).Count -ne 2) { throw 'заголовок' }
        if ($b.rows[0].baseVersion -cne 'AAAB' -or $null -ne $b.rows[1].baseVersion) { throw 'baseVersion' }
        $json = $b | ConvertTo-Json -Depth 8 -Compress
        if ($json -notlike '*"columnCode":"M01"*' -or $json -notlike '*"isEmpty":false*') { throw "json $json" }
    }
    It 'опис золотого тесту в моделі перевіряється: рядок, місяць, ввід' {
        $m = Get-Hse400MTestModel
        $m.methodology.tests[0].row = 'WCHS'
        Assert-Hse400Throws { Assert-Hse400Model $m } 'не розрахунковий рядок'
        $m = Get-Hse400MTestModel
        $m.methodology.tests[0].month = 'M13'
        Assert-Hse400Throws { Assert-Hse400Model $m } 'місяць'
        $m = Get-Hse400MTestModel
        $m.methodology.tests[0].inputs | Add-Member -NotePropertyName F_H2S -NotePropertyValue 1
        Assert-Hse400Throws { Assert-Hse400Model $m } 'не рядок вводу'
        $m = Get-Hse400MTestModel
        $m.methodology.tests[1].code = $m.methodology.tests[0].code
        Assert-Hse400Throws { Assert-Hse400Model $m } 'повтор коду'
    }
    It 'модель без розділу methodology або з поганим кодом відхиляється' {
        $m = Get-Hse400MTestModel
        $m.PSObject.Properties.Remove('methodology')
        Assert-Hse400Throws { Assert-Hse400Model $m } 'methodology'
        $m = Get-Hse400MTestModel
        $m.methodology.code = '1HSE'
        Assert-Hse400Throws { Assert-Hse400Model $m } 'EcrCode'
        $m = Get-Hse400MTestModel
        $m.methodology.formulas = @([pscustomobject]@{ code = 'HSE400_FG_Makat_H2S'; expression = '' })
        Assert-Hse400Throws { Assert-Hse400Model $m } 'порожній вираз'
    }
    It 'таблиця - MonthsInColumns, Fixed' {
        $m = Read-Hse400Model
        $b = New-Hse400TableBody -Table $m.tables[0] -Ordinal 1
        if ($b.layoutKind -cne 'MonthsInColumns' -or $b.rowMode -cne 'Fixed' -or $b.ordinal -ne 1) { throw "таблиця $($b.layoutKind)/$($b.rowMode)" }
        $json = $b | ConvertTo-Json -Compress
        if ($json -notlike '*"layoutKind":"MonthsInColumns"*') { throw "json $json" }
    }
    It 'місячна колонка - ввід Decimal з номером місяця; якір - прихований Calculated не місячний' {
        $mc = @(Get-Hse400MonthColumns)[2]
        $b = New-Hse400MonthColumnBody -Month $mc
        if ($b.dataType -cne 'Decimal' -or -not $b.isMonthColumn -or $b.monthNumber -ne 3 -or $b.isReadOnly) { throw 'місячна колонка' }
        $a = New-Hse400AnchorColumnBody
        if ($a.dataType -cne 'Calculated' -or -not $a.isHidden -or -not $a.isReadOnly -or $null -ne $a.isMonthColumn -or $null -ne $a.monthNumber) { throw 'якір' }
    }
    It 'рядок F_ - лише читання, рядок вводу - ні' {
        $m = Read-Hse400Model
        $in = @($m.tables[0].rows | Where-Object { $_.kind -eq 'Input' })[0]
        $calc = @($m.tables[0].rows | Where-Object { $_.kind -eq 'Calc' })[0]
        if ((New-Hse400RowBody -Row $in -Ordinal 1).isReadOnly) { throw 'ввід лише для читання' }
        if (-not (New-Hse400RowBody -Row $calc -Ordinal 2).isReadOnly) { throw 'F_ редагований' }
    }
    It 'методологія Bespoke, версія порожня рівня Module, правило і прив''язка "{}"' {
        $m = Read-Hse400Model
        $mb = New-Hse400MethodologyBody $m
        if ($mb.code -cne 'HSE400' -or $mb.kind -cne 'Bespoke' -or -not $mb.nameL10n.en -or -not $mb.nameL10n.ru) { throw 'методологія' }
        $vb = New-Hse400MethodologyVersionBody $m
        if ($vb.level -cne 'Module' -or $null -ne $vb.copyFromVersionId -or $vb.versionNumber -cne [string]$m.methodology.version) { throw 'версія' }
        $rb = New-Hse400CatchAllRuleBody
        if ($rb.matchJson -cne '{}' -or -not $rb.isActive) { throw 'правило' }
        if ((Get-Hse400CatchAllRuleCode) -cnotmatch '^[A-Za-z][A-Za-z0-9_]{0,63}$') { throw 'код правила' }
        $bb = New-Hse400BindingBody -Active $true
        if ($bb.matchJson -cne '{}' -or -not $bb.isActive) { throw "прив'язка" }
        $pb = New-Hse400MethodologyPublishBody $m
        if (-not $pb.changeReason -or $pb.effectiveFrom -cne '2024-01-01') { throw 'публікація' }
    }
    It 'формула і константа з моделі -> тіла запитів' {
        $fb = New-Hse400FormulaBody -Formula ([pscustomobject]@{ code = 'HSE400_FG_Makat_H2S'; expression = '@GCV_HSE400_FG_Makat_WCHS*2'; arguments = 'GCV_HSE400_FG_Makat_WCHS' })
        if ($fb.expression -cne '@GCV_HSE400_FG_Makat_WCHS*2' -or $fb.resultType -cne 'Number' -or $fb.argumentsCsv -cne 'GCV_HSE400_FG_Makat_WCHS') { throw 'формула' }
        $cb = New-Hse400ConstantBody -Constant ([pscustomobject]@{ code = 'k1_HSE400_'; value = 22.4; unit = 'one' }) -UnitIds @{ one = 7 }
        if ($cb.kind -cne 'Numeric' -or $cb.value -ne 22.4 -or $cb.unitId -ne 7) { throw 'константа' }
        Assert-Hse400Throws { New-Hse400ConstantBody -Constant ([pscustomobject]@{ code = 'k2_HSE400_'; value = 1; unit = 'zz' }) -UnitIds @{ one = 7 } } 'zz'
    }
    It 'числова константа без одиниці -> одиниця one (сервер відхиляє без одиниці: ECR-CALC-0422 constantNoUnit)' {
        $cb = New-Hse400ConstantBody -Constant ([pscustomobject]@{ code = 'LHV_GCV_COS'; value = 1.5 }) -UnitIds @{ one = 7 }
        if ($cb.kind -cne 'Numeric' -or $cb.unitId -ne 7) { throw "unitId $($cb.unitId)" }
        $tb = New-Hse400ConstantBody -Constant ([pscustomobject]@{ code = 'k3_HSE400_'; kind = 'Text'; textValue = 'x' }) -UnitIds @{ one = 7 }
        if ($null -ne $tb.unitId) { throw 'текстова константа з одиницею' }
        Assert-Hse400Throws { New-Hse400ConstantBody -Constant ([pscustomobject]@{ code = 'k4_HSE400_'; value = 1 }) -UnitIds @{} } "'one'"
        $m = Get-Hse400MTestModel
        foreach ($c in @($m.methodology.constants)) {
            $b = New-Hse400ConstantBody -Constant $c -UnitIds @{ one = 7 }
            if ($b.kind -ceq 'Numeric' -and $null -eq $b.unitId) { throw "константа $($c.code) без одиниці" }
        }
    }
    It 'одиниця one: є - id, немає - зрозуміла відмова' {
        $id = Find-Hse400UnitId @([pscustomobject]@{ id = 3; code = 't' }, [pscustomobject]@{ id = 9; code = 'one' })
        if ($id -ne 9) { throw "id $id" }
        Assert-Hse400Throws { Find-Hse400UnitId @([pscustomobject]@{ id = 3; code = 't' }) } "'one'"
        Assert-Hse400Throws { Find-Hse400UnitId @() } "'one'"
    }
    It 'структура: згідна - без зауважень; PerPeriodInstance і без якоря - названо' {
        $m = Read-Hse400Model
        $bad = @(Test-Hse400Structure -Structure (New-Hse400TestStructure $m) -Model $m)
        if ($bad.Count) { throw ($bad -join '; ') }
        $bad = @(Test-Hse400Structure -Structure (New-Hse400TestStructure $m -Layout 'PerPeriodInstance') -Model $m)
        if ($bad.Count -ne 6 -or $bad[0] -notlike '*layoutKind PerPeriodInstance*') { throw ($bad -join '; ') }
        $bad = @(Test-Hse400Structure -Structure (New-Hse400TestStructure $m -NoAnchor) -Model $m)
        if ($bad.Count -ne 6 -or $bad[0] -notlike '*HSE400_Calc*') { throw ($bad -join '; ') }
    }
    It 'якорі прив''язок: таблиця -> Id колонки HSE400_Calc' {
        $m = Read-Hse400Model
        $ids = Get-Hse400AnchorColumnIds -Structure (New-Hse400TestStructure $m) -Model $m
        if ($ids.Count -ne 6 -or $ids['FG_Makat'] -ne 1013) { throw "якорі $($ids.Count) / $($ids['FG_Makat'])" }
    }
}
