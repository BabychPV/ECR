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

    It 'аргумент рядка вводу: GCV_<простір>_<таблиця>_<RowKey>' {
        $n = Get-Hse400ArgumentName -Namespace 'HSE400' -Table 'FG_Makat' -RowKey 'Methane'
        if ($n -cne 'GCV_HSE400_FG_Makat_Methane') { throw "ім'я $n" }
        $n = Get-Hse400ArgumentName -Namespace 'HSE400' -Table 'LPG_Internal' -RowKey 'IC4_1'
        if ($n -cne 'GCV_HSE400_LPG_Internal_IC4_1') { throw "ім'я $n" }
    }
    It 'рядок F_ аргументом не буває' {
        Assert-Hse400Throws { Get-Hse400ArgumentName -Namespace 'HSE400' -Table 'FG_Makat' -RowKey 'F_Nitrogen' } 'розрахунковий'
    }
    It 'формула рядка F_<X>: <простір>_<таблиця>_<X>; рядок вводу - виняток' {
        $c = Get-Hse400FormulaCode -Namespace 'HSE400' -Table 'FG_Makat' -RowKey 'F_Nitrogen'
        if ($c -cne 'HSE400_FG_Makat_Nitrogen') { throw "код $c" }
        Assert-Hse400Throws { Get-Hse400FormulaCode -Namespace 'HSE400' -Table 'FG_Makat' -RowKey 'Nitrogen' } 'F_'
    }
    It 'план чинної моделі: 175 аргументів, 325 цілей, 6 прив''язок на якір, формул немає - не готово' {
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
        if (@($p.MissingFormulas).Count -ne 325 -or $p.Ready) { throw "прогалин $(@($p.MissingFormulas).Count), ready $($p.Ready)" }
    }
    It 'формули на всі F_ і тест - готово; бракує однієї - не готово й названо яку' {
        $m = Get-Hse400MTestModel
        $p0 = Get-Hse400MethodologyPlan $m
        $fs = @($p0.Targets | ForEach-Object { [pscustomobject]@{ code = $_.FormulaCode; expression = '1' } })
        $m.methodology | Add-Member -NotePropertyName formulas -NotePropertyValue $fs
        $m.methodology | Add-Member -NotePropertyName tests -NotePropertyValue @([pscustomobject]@{ code = 'GOLD_1'; inputJson = '{}'; expectedJson = '{}'; tolerance = 0 })
        Assert-Hse400Model $m
        $p = Get-Hse400MethodologyPlan $m
        if (-not $p.Ready -or @($p.MissingFormulas).Count -ne 0) { throw 'повна модель не готова' }
        $m.methodology.formulas = @($fs | Select-Object -Skip 1)
        $p = Get-Hse400MethodologyPlan $m
        if ($p.Ready -or @($p.MissingFormulas).Count -ne 1 -or $p.MissingFormulas[0].FormulaCode -cne $fs[0].code) { throw 'прогалину не названо' }
    }
    It 'без тесту не готово навіть із усіма формулами' {
        $m = Get-Hse400MTestModel
        $p0 = Get-Hse400MethodologyPlan $m
        $m.methodology | Add-Member -NotePropertyName formulas -NotePropertyValue @($p0.Targets | ForEach-Object { [pscustomobject]@{ code = $_.FormulaCode; expression = '1' } })
        if ((Get-Hse400MethodologyPlan $m).Ready) { throw 'готово без тесту' }
    }
    It 'модель без розділу methodology або з поганим кодом відхиляється' {
        $m = Get-Hse400MTestModel
        $m.PSObject.Properties.Remove('methodology')
        Assert-Hse400Throws { Assert-Hse400Model $m } 'methodology'
        $m = Get-Hse400MTestModel
        $m.methodology.code = '1HSE'
        Assert-Hse400Throws { Assert-Hse400Model $m } 'EcrCode'
        $m = Get-Hse400MTestModel
        $m.methodology | Add-Member -NotePropertyName formulas -NotePropertyValue @([pscustomobject]@{ code = 'HSE400_FG_Makat_H2S'; expression = '' })
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
