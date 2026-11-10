<#
    Pester (3/5 сумісний синтаксис без `Should`): tools/land/LandMonthBands.Lib.ps1 — план місячних смуг Land
    (суміжні блоки, батьківські смуги, ідемпотентність, дублі Ordinal, чужі групи). Мережі й ECR не потрібно.
    Кожен It самодостатній (у Pester 5 змінні Describe не доходять до It).
#>

Describe 'LandMonthBands: план місячних груп заголовків' {
    It 'суміжні блоки: друга смуга того ж місяця — окрема група з тим самим підписом; немісячні колонки не групуються' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/LandMonthBands.Lib.ps1')
        $src = Read-LandMonthBandSource (Join-Path $repo 'tools/land/land-month-bands.json')
        $cols = @(
            [pscustomobject]@{ Code = 'A'; Ordinal = 1; MonthNumber = $null; GroupCode = $null }
            [pscustomobject]@{ Code = 'H1'; Ordinal = 2; MonthNumber = 1; GroupCode = $null }
            [pscustomobject]@{ Code = 'F1'; Ordinal = 3; MonthNumber = 1; GroupCode = $null }
            [pscustomobject]@{ Code = 'H2'; Ordinal = 4; MonthNumber = 2; GroupCode = $null }
            [pscustomobject]@{ Code = 'JanuaryRHDuringExceeding'; Ordinal = 5; MonthNumber = $null; GroupCode = $null }
            [pscustomobject]@{ Code = 'X1'; Ordinal = 6; MonthNumber = 1; GroupCode = $null }
        )
        $p = Get-LandMonthBandPlan -TableCode 'TX' -Columns $cols -ExistingGroups @() -Source $src
        if ($p.Skip -or $p.Errors.Count) { throw "план: Skip=$($p.Skip) Errors=$($p.Errors -join ';')" }
        if ($p.MonthGroups -ne 3) { throw "груп $($p.MonthGroups), очікувалось 3 (Jan, Feb, Jan#2)" }
        $codes = @($p.Groups | ForEach-Object { $_.code }) -join ','
        if ($codes -ne 'MB_M01,MB_M02,MB_M01_2') { throw "коди: $codes" }
        $jan2 = $p.Groups | Where-Object { $_.code -eq 'MB_M01_2' }
        if ($jan2.labelL10n.en -ne 'January' -or $jan2.labelL10n.ru -ne 'Январь') { throw 'підпис другого блоку січня' }
        $to = @{}; foreach ($c in $p.Changes) { $to[$c.Column] = $c.To }
        if ($to['H1'] -ne 'MB_M01' -or $to['F1'] -ne 'MB_M01' -or $to['X1'] -ne 'MB_M01_2' -or $to['H2'] -ne 'MB_M02') { throw 'призначення колонок' }
        if ($to.ContainsKey('A') -or $to.ContainsKey('JanuaryRHDuringExceeding')) { throw 'немісячну колонку згруповано' }
        if (-not $p.GroupsChanged) { throw 'перший запуск мусить змінювати дерево' }

        # той самий місяць по обидва боки немісячної колонки — два блоки (інакше ECR-TMPL-4229 notContiguous)
        $gap = @(
            [pscustomobject]@{ Code = 'J1'; Ordinal = 1; MonthNumber = 1; GroupCode = $null }
            [pscustomobject]@{ Code = 'Q'; Ordinal = 2; MonthNumber = $null; GroupCode = $null }
            [pscustomobject]@{ Code = 'J2'; Ordinal = 3; MonthNumber = 1; GroupCode = $null }
        )
        $pg = Get-LandMonthBandPlan -TableCode 'TX' -Columns $gap -ExistingGroups @() -Source $src
        if ($pg.MonthGroups -ne 2) { throw "січень, розірваний немісячною колонкою: груп $($pg.MonthGroups), очікувалось 2" }
    }

    It 'повторний запуск на застосованому стані дає 0 змін (ідемпотентність)' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/LandMonthBands.Lib.ps1')
        $src = Read-LandMonthBandSource (Join-Path $repo 'tools/land/land-month-bands.json')
        $cols = @(
            [pscustomobject]@{ Code = 'C1'; Ordinal = 1; MonthNumber = 1; GroupCode = $null }
            [pscustomobject]@{ Code = 'C2'; Ordinal = 2; MonthNumber = 2; GroupCode = $null }
        )
        $p1 = Get-LandMonthBandPlan -TableCode 'TX' -Columns $cols -ExistingGroups @() -Source $src
        # «застосовуємо» план: дерево і призначення як їх поверне структура
        $existing = @($p1.Groups | ForEach-Object { [pscustomobject]@{ Code = $_.code; ParentCode = $_.parentCode; Ordinal = $_.ordinal; En = $_.labelL10n.en; Ru = $_.labelL10n.ru } })
        $after = @($cols | ForEach-Object { $c = $_; $ch = $p1.Changes | Where-Object { $_.Column -eq $c.Code }
                [pscustomobject]@{ Code = $c.Code; Ordinal = $c.Ordinal; MonthNumber = $c.MonthNumber; GroupCode = $ch.To } })
        $p2 = Get-LandMonthBandPlan -TableCode 'TX' -Columns $after -ExistingGroups $existing -Source $src
        if ($p2.GroupsChanged -or @($p2.Changes).Count -ne 0) { throw "другий запуск: дерево змінено=$($p2.GroupsChanged), змін колонок $(@($p2.Changes).Count)" }
    }

    It 'T3_00: розділ > квартал > місяць, колонка кварталу в групі кварталу, глибина 3; таблиця без місяців — Skip' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/LandMonthBands.Lib.ps1')
        $src = Read-LandMonthBandSource (Join-Path $repo 'tools/land/land-month-bands.json')
        $mon = @('Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec')
        $cols = @(); $o = 0
        foreach ($m in 1..12) { foreach ($k in 'RH', 'F', 'G') { $o++; $cols += [pscustomobject]@{ Code = "P$k$m"; Ordinal = $o; MonthNumber = $m; GroupCode = $null } } }
        $o++; $cols += [pscustomobject]@{ Code = 'Other'; Ordinal = $o; MonthNumber = $null; GroupCode = $null }
        foreach ($m in 1..12) {
            if (($m - 1) % 3 -eq 0) { $o++; $cols += [pscustomobject]@{ Code = "C_$((($m - 1) / 3) + 1)Quarter"; Ordinal = $o; MonthNumber = $null; GroupCode = $null } }
            foreach ($k in 'RHDuringExceeding', '0301NO2', '0304NO', '0330SO2', '0337CO') { $o++; $cols += [pscustomobject]@{ Code = "$($mon[$m-1])_$k"; Ordinal = $o; MonthNumber = $m; GroupCode = $null } }
        }
        $p = Get-LandMonthBandPlan -TableCode 'T3_00' -Columns $cols -ExistingGroups @() -Source $src
        if ($p.Errors.Count) { throw ($p.Errors -join ';') }
        if ($p.MonthGroups -ne 24 -or $p.ParentGroups -ne 5) { throw "місячних $($p.MonthGroups) (24), батьківських $($p.ParentGroups) (5)" }
        $g = @{}; foreach ($x in $p.Groups) { $g[$x.code] = $x }
        if ($g['MB_M01_2'].parentCode -ne 'MB_Q1' -or $g['MB_Q1'].parentCode -ne 'MB_S_MEAS' -or $null -ne $g['MB_M01'].parentCode) { throw 'вкладеність S > Q > M' }
        $to = @{}; foreach ($c in $p.Changes) { $to[$c.Column] = $c.To }
        if ($to['C_1Quarter'] -ne 'MB_Q1' -or $to['C_4Quarter'] -ne 'MB_Q4' -or $to.ContainsKey('Other')) { throw 'колонки кварталів' }
        # T9_04 у 1.0.14.0: блок RH без місяця — квартальна смуга не ставиться, колонки RH не групуються
        $t9 = @(
            [pscustomobject]@{ Code = 'Land_M01'; Ordinal = 1; MonthNumber = 1; GroupCode = $null }
            [pscustomobject]@{ Code = 'C_1Quarter'; Ordinal = 2; MonthNumber = $null; GroupCode = $null }
            [pscustomobject]@{ Code = 'JanuaryRHDuringExceeding'; Ordinal = 3; MonthNumber = $null; GroupCode = $null }
            [pscustomobject]@{ Code = 'C_0337CO_GL'; Ordinal = 4; MonthNumber = $null; GroupCode = $null }
        )
        $p9 = Get-LandMonthBandPlan -TableCode 'T9_04' -Columns $t9 -ExistingGroups @() -Source $src
        if ($p9.ParentGroups -ne 0 -or @($p9.Changes | Where-Object { $_.Column -in 'C_1Quarter', 'JanuaryRHDuringExceeding' }).Count) { throw 'квартал без прив''язаних місяців мусить пропускатися' }
        $none = Get-LandMonthBandPlan -TableCode 'T8_00' -Columns @([pscustomobject]@{ Code = 'A'; Ordinal = 1; MonthNumber = $null; GroupCode = $null }) -ExistingGroups @() -Source $src
        if (-not $none.Skip) { throw 'таблиця без місячних колонок мусить лишатися без змін' }
    }

    It 'дублі Ordinal звітуються і впорядковуються як на сервері (Ordinal, потім Code); чужа група — помилка, а не перезапис' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/LandMonthBands.Lib.ps1')
        $src = Read-LandMonthBandSource (Join-Path $repo 'tools/land/land-month-bands.json')
        # B і a з однаковим Ordinal: ordinal-порівняння кодів ставить 'B' (0x42) перед 'a' (0x61)
        $cols = @(
            [pscustomobject]@{ Code = 'a'; Ordinal = 1; MonthNumber = 1; GroupCode = $null }
            [pscustomobject]@{ Code = 'B'; Ordinal = 1; MonthNumber = $null; GroupCode = $null }
            [pscustomobject]@{ Code = 'c'; Ordinal = 2; MonthNumber = 1; GroupCode = $null }
        )
        $p = Get-LandMonthBandPlan -TableCode 'TX' -Columns $cols -ExistingGroups @() -Source $src
        if (@($p.DuplicateOrdinals).Count -ne 1 -or $p.DuplicateOrdinals[0] -notmatch 'Ordinal 1: B, a') { throw "дублі: $($p.DuplicateOrdinals -join ';')" }
        if ($p.MonthGroups -ne 1) { throw "порядок як на сервері дає B, a, c → один блок січня (a, c); груп $($p.MonthGroups)" }

        $foreign = @([pscustomobject]@{ Code = 'G1'; ParentCode = $null; Ordinal = 1; En = 'PAVLO'; Ru = $null })
        $cols2 = @([pscustomobject]@{ Code = 'm'; Ordinal = 1; MonthNumber = 1; GroupCode = 'G1' })
        $p2 = Get-LandMonthBandPlan -TableCode 'TX' -Columns $cols2 -ExistingGroups $foreign -Source $src
        if ($p2.Errors.Count -ne 1) { throw 'місячна колонка в чужій групі мусить давати помилку' }
        $cols3 = @([pscustomobject]@{ Code = 'x'; Ordinal = 1; MonthNumber = $null; GroupCode = 'G1' }, [pscustomobject]@{ Code = 'm'; Ordinal = 2; MonthNumber = 1; GroupCode = $null })
        $p3 = Get-LandMonthBandPlan -TableCode 'TX' -Columns $cols3 -ExistingGroups $foreign -Source $src
        if (-not (@($p3.Groups | Where-Object { $_.code -eq 'G1' }).Count) -or @($p3.Changes | Where-Object { $_.Column -eq 'x' }).Count) { throw 'чужу групу і її колонку збережено' }
    }
}
