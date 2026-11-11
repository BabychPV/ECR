# tools/tests/Hse400-Model.Tests.ps1
# Сторож моделі шаблону HSE400: договір рядків (ввід / F_<X>), ключі, кількості, місячні колонки.
# Підготовка - у BeforeAll: CI ганяє Pester 5 (pwsh, Linux), де код поза блоками виконується лише на виявленні;
# локально працює й Pester 3.4. Шляхи - з прямою рискою (Linux).
Describe 'HSE400 model' {
    BeforeAll {
        $root = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $root 'tools/hse400/Hse400Model.Lib.ps1')

        function Get-Hse400TestModel {
            $p = (Resolve-Path (Join-Path $PSScriptRoot '../hse400/hse400-model.json')).Path
            return ([IO.File]::ReadAllText($p, [Text.Encoding]::UTF8) | ConvertFrom-Json)
        }
        function Assert-Hse400Rejected {
            param($Model, [string]$Expect)
            $msg = $null
            try { Assert-Hse400Model $Model } catch { $msg = $_.Exception.Message }
            if ($null -eq $msg) { throw "Зіпсовану модель прийнято (очікувалося: $Expect)." }
            if ($msg -notlike "*$Expect*") { throw "Відмова не про те: $msg" }
        }
    }
    It 'чинна модель проходить перевірку' {
        $m = Read-Hse400Model
        if (@($m.tables).Count -ne 6) { throw "таблиць $(@($m.tables).Count)" }
        $rows = 0; foreach ($t in @($m.tables)) { $rows += @($t.rows).Count }
        if ($rows -ne 500) { throw "рядків $rows, очікується 500" }
    }
    It 'розрахунковий рядок без F_ відхиляється' {
        $m = Get-Hse400TestModel
        $r = @($m.tables[0].rows | Where-Object { $_.kind -eq 'Calc' })[0]
        $r.key = $r.key.Substring(2)
        Assert-Hse400Rejected $m 'без префікса F_'
    }
    It 'ввід із префіксом F_ відхиляється' {
        $m = Get-Hse400TestModel
        $m.tables[1].rows[0].key = 'F_Zzz'
        Assert-Hse400Rejected $m 'ввід із префіксом F_'
    }
    It 'повтор ключа без урахування регістру відхиляється' {
        $m = Get-Hse400TestModel
        $m.tables[2].rows[1].key = ([string]$m.tables[2].rows[0].key).ToUpperInvariant()
        Assert-Hse400Rejected $m 'повтор ключа'
    }
    It 'ключ поза шаблоном RowKey відхиляється' {
        $m = Get-Hse400TestModel
        $m.tables[3].rows[0].key = 'A B'
        Assert-Hse400Rejected $m 'RowKey'
    }
    It 'зміна кількості рядків відхиляється' {
        $m = Get-Hse400TestModel
        $m.tables[4].rows = @($m.tables[4].rows | Select-Object -Skip 1)
        Assert-Hse400Rejected $m 'очікується 38/65'
    }
    It 'місячних колонок 12, M01..M12' {
        $c = @(Get-Hse400MonthColumns)
        if ($c.Count -ne 12 -or $c[0].Code -ne 'M01' -or $c[11].Code -ne 'M12' -or $c[11].Month -ne 12) { throw 'місячні колонки' }
        foreach ($x in $c) { if (-not $x.En -or -not $x.Ru) { throw "порожня назва $($x.Code)" } }
    }
}
