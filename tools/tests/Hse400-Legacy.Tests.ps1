# tools/tests/Hse400-Legacy.Tests.ps1
# Сторож зіставлення HSE400 з колонками старої системи (AF_AirGasCompositions_HSE400 / CalcResult_AirGasCompositions_HSE400).
# Дані - синтетичні (значень замовника в git немає). Pester 5 (CI, pwsh) і 3.4 (локально).
Describe 'HSE400 legacy columns' {
    BeforeAll {
        $root = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $root 'tools/hse400/Hse400Legacy.Lib.ps1')

        function New-Row([string]$key, [string]$kind) { return [pscustomobject]@{ key = $key; kind = $kind } }
        function New-LegacyModel {
            $makat = [pscustomobject]@{ code = 'FG_Makat'; rows = @(
                    (New-Row 'Methane' 'Input'), (New-Row 'Nitrogen' 'Input'),
                    (New-Row 'F_Nitrogen' 'Calc'), (New-Row 'F_Density20' 'Calc'), (New-Row 'F_FuelLowerHeating0' 'Calc'),
                    (New-Row 'F_Methane_mass' 'Calc'), (New-Row 'F_GHG1' 'Calc'), (New-Row 'F_GHG2' 'Calc')) }
            $hmb = [pscustomobject]@{ code = 'FG_HMB'; rows = @(
                    (New-Row 'Methane' 'Input'), (New-Row 'F_MercaptanSulphurContent' 'Calc'), (New-Row 'F_TotalSulphurContent' 'Calc')) }
            $internal = [pscustomobject]@{ code = 'FG_Internal'; rows = @(
                    (New-Row 'H2S' 'Input'), (New-Row 'F_H2S' 'Calc'), (New-Row 'F_MercaptanSulphurContent' 'Calc')) }
            return [pscustomobject]@{ tables = @($makat, $hmb, $internal) }
        }
        $script:InCols = @('FG_Makat_Methane', 'FG_Makat_Nitrogen', 'FG_HMB_Methane', 'FG_Internal_H2S')
        $script:OutCols = @('FG_Makat_Methane', 'FG_Makat_Nitrogen', 'FG_Makat_NitrogenV2', 'FG_Makat_Density_20', 'FG_Makat_FuelLowerHeating_0',
            'FG_Makat_Methane_mass', 'FG_HMB_Methane', 'FG_HMB_TotalSulphurContentWithoutH2S', 'FG_HMB_TotalSulphurContent',
            'FG_Internal_H2S', 'FG_Internal_H2S_V2', 'FG_Internal_MercaptanSulphurContent')
    }

    It 'формула з іменем рядка вводу: результат лежить у V2 (Makat) або _V2 (Internal), а не в колонці вводу' {
        $a = Get-Hse400LegacyResultColumn -Table 'FG_Makat' -RowKey 'F_Nitrogen' -InputKeys @('Methane', 'Nitrogen') -Columns $script:OutCols
        if ($a -ne 'FG_Makat_NitrogenV2') { throw "Makat: $a" }
        $b = Get-Hse400LegacyResultColumn -Table 'FG_Internal' -RowKey 'F_H2S' -InputKeys @('H2S') -Columns $script:OutCols
        if ($b -ne 'FG_Internal_H2S_V2') { throw "Internal: $b" }
    }

    It 'Density20 і FuelLowerHeating0 - колонки з підкресленням' {
        $a = Get-Hse400LegacyResultColumn -Table 'FG_Makat' -RowKey 'F_Density20' -InputKeys @('Methane') -Columns $script:OutCols
        $b = Get-Hse400LegacyResultColumn -Table 'FG_Makat' -RowKey 'F_FuelLowerHeating0' -InputKeys @('Methane') -Columns $script:OutCols
        if ($a -ne 'FG_Makat_Density_20' -or $b -ne 'FG_Makat_FuelLowerHeating_0') { throw "$a / $b" }
    }

    It 'MercaptanSulphurContent: у HMB - TotalSulphurContentWithoutH2S, в Internal - власна колонка' {
        $a = Get-Hse400LegacyResultColumn -Table 'FG_HMB' -RowKey 'F_MercaptanSulphurContent' -InputKeys @('Methane') -Columns $script:OutCols
        $b = Get-Hse400LegacyResultColumn -Table 'FG_Internal' -RowKey 'F_MercaptanSulphurContent' -InputKeys @('H2S') -Columns $script:OutCols
        if ($a -ne 'FG_HMB_TotalSulphurContentWithoutH2S') { throw "HMB: $a" }
        if ($b -ne 'FG_Internal_MercaptanSulphurContent') { throw "Internal: $b" }
    }

    It 'GHG1/GHG2 і невідомі рядки - без пари (а не мовчазне зіставлення з чимось іншим)' {
        foreach ($k in 'F_GHG1', 'F_GHG2') {
            if ($null -ne (Get-Hse400LegacyResultColumn -Table 'FG_Makat' -RowKey $k -InputKeys @('Methane') -Columns $script:OutCols)) { throw $k }
        }
        if ($null -ne (Get-Hse400LegacyResultColumn -Table 'FG_Makat' -RowKey 'F_Unknown' -InputKeys @('Methane') -Columns $script:OutCols)) { throw 'F_Unknown' }
        if ($null -ne (Get-Hse400LegacyResultColumn -Table 'FG_Makat' -RowKey 'Methane' -InputKeys @('Methane') -Columns $script:OutCols)) { throw 'Input як Calc' }
    }

    It 'Join: значення за місяцями, без пари - у NoPair, невикористані колонки - у переліку' {
        $months = @{
            1 = @{ inputs = @{ FG_Makat_Methane = 90.5; FG_HMB_Methane = 1.0 }; results = @{ FG_Makat_NitrogenV2 = 2.5; FG_Makat_Density_20 = 0.7 } }
            3 = @{ inputs = @{ FG_Makat_Methane = 91.0 }; results = @{ FG_Makat_NitrogenV2 = 0.0 } }
        }
        $j = Join-Hse400LegacyItems -Model (New-LegacyModel) -Months $months -InputColumns $script:InCols -ResultColumns $script:OutCols
        $n = $j.Items | Where-Object { $_.Table -eq 'FG_Makat' -and $_.RowKey -eq 'F_Nitrogen' }
        if ($n.SourceRow -ne 'FG_Makat_NitrogenV2' -or $n.Values[0] -ne 2.5 -or $n.Values[2] -ne 0.0 -or $null -ne $n.Values[1]) { throw 'F_Nitrogen' }
        $mth = $j.Items | Where-Object { $_.Table -eq 'FG_Makat' -and $_.RowKey -eq 'Methane' }
        if ($mth.Kind -ne 'Input' -or $mth.Values[0] -ne 90.5 -or $mth.Values[2] -ne 91.0) { throw 'Methane' }
        if (@($j.NoPair).Count -ne 2 -or ($j.NoPair -notcontains 'FG_Makat/F_GHG1')) { throw "NoPair: $($j.NoPair -join ',')" }
        # Колонка CalcResult FG_Makat_Methane (ехо вводу) лишається невикористаною для розрахункових, але це не «без рядка» для вводу.
        if ($j.UnusedResultColumns -notcontains 'FG_Makat_Nitrogen') { throw 'UnusedResultColumns' }
    }

    It 'клас розбіжності: OnlyNew / OnlyOld / Rounding / Value' {
        if ((Get-Hse400LegacyDiffClass -Expected $null -Actual 1.0) -ne 'OnlyNew') { throw 'OnlyNew' }
        if ((Get-Hse400LegacyDiffClass -Expected 1.0 -Actual $null) -ne 'OnlyOld') { throw 'OnlyOld' }
        if ((Get-Hse400LegacyDiffClass -Expected 1e-12 -Actual 3e-12) -ne 'Rounding') { throw 'Rounding' }
        if ((Get-Hse400LegacyDiffClass -Expected 1.0 -Actual 1.1) -ne 'Value') { throw 'Value' }
    }

    It 'місяць за міткою часу: чужий рік - $null' {
        if ((Get-Hse400LegacyMonthIndex -Time ([datetime]'2026-03-01 05:00') -Year 2026) -ne 3) { throw 'month' }
        if ($null -ne (Get-Hse400LegacyMonthIndex -Time ([datetime]'2025-03-01') -Year 2026)) { throw 'year' }
    }
}
