<#
    Pester (без Should): Format-LandAirSubstanceDisplay + ConvertTo-LandAirSubstancePlan - показ "<код> — <назва>" без подвійного префікса.
    Мережі й ECR не потрібно. Запуск: Invoke-Pester -Path tools/tests.
#>

Describe 'LandAirSubstances: показ "<код> — <назва>"' {
    It 'додає префікс коду, не префіксує вдруге' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/LandAirSubstances.Lib.ps1')
        $dash = [string][char]0x2014
        $once = Format-LandAirSubstanceDisplay -Code '143' -Name 'Aluminium oxide'
        if ($once -cne "143 $dash Aluminium oxide") { throw "Очікувалось '143 $dash Aluminium oxide', отримано '$once'" }
        $twice = Format-LandAirSubstanceDisplay -Code '143' -Name $once
        if ($twice -cne $once) { throw "Подвійний префікс: '$twice'" }
        if ((Format-LandAirSubstanceDisplay -Code '143' -Name '') -cne '143') { throw 'Порожня назва має лишитись кодом' }
        if ((Format-LandAirSubstanceDisplay -Code '143' -Name '143') -cne '143') { throw 'Назва = код має лишитись кодом' }
    }

    It 'план імпорту: DisplayEn/DisplayRu з префіксом, ru порожня лишається порожньою' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/LandAirSubstances.Lib.ps1')
        $dash = [string][char]0x2014
        $rows = @(
            [pscustomobject]@{ Code = '0143'; NameEn = 'Aluminium oxide'; NameRu = 'Алюминий оксид'; Formula = ''; StateEn = ''; StateRu = '' },
            [pscustomobject]@{ Code = '301'; NameEn = 'Nitrogen dioxide'; NameRu = ''; Formula = ''; StateEn = ''; StateRu = '' }
        )
        $plan = ConvertTo-LandAirSubstancePlan -Rows $rows
        $a = $plan.Items | Where-Object { $_.Code -eq '143' }
        $b = $plan.Items | Where-Object { $_.Code -eq '301' }
        if ($a.DisplayEn -cne "143 $dash Aluminium oxide") { throw "EN: '$($a.DisplayEn)'" }
        if ($a.DisplayRu -cne "143 $dash Алюминий оксид") { throw "RU: '$($a.DisplayRu)'" }
        if ($b.DisplayRu -cne '') { throw "RU порожня має лишитись порожньою: '$($b.DisplayRu)'" }
        $again = Format-LandAirSubstanceDisplay -Code '143' -Name $a.DisplayRu
        if ($again -cne $a.DisplayRu) { throw 'Другий прогін змінив назву' }
    }
}
