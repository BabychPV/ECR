<#
    Pester (без Should): Format-Hse401NaeDisplay - показ вузла SRC "<MPE> — <назва>" (tools/hse401/Hse401Nae.Lib.ps1).
    Мережі й ECR не потрібно. Запуск: Invoke-Pester -Path tools/tests.
#>

Describe 'Hse401 NAE: показ "<MPE> — <назва>"' {
    It 'назва джерела отримує префікс MPE' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/hse401/Hse401Nae.Lib.ps1')
        $dash = [string][char]0x2014
        $r = Format-Hse401NaeDisplay -Mpe '0081' -Name 'ATC&DC'
        if ($r -cne "0081 $dash ATC&DC") { throw "Отримано '$r'" }
    }

    It 'MPE із суфіксом лишається дослівним (регістр джерела)' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/hse401/Hse401Nae.Lib.ps1')
        $dash = [string][char]0x2014
        $r = Format-Hse401NaeDisplay -Mpe '3635_POD' -Name 'Bolashak station'
        if ($r -cne "3635_POD $dash Bolashak station") { throw "Отримано '$r'" }
        $r2 = Format-Hse401NaeDisplay -Mpe '6354_dr' -Name 'X'
        if ($r2 -cne "6354_dr $dash X") { throw "Регістр MPE змінено: '$r2'" }
    }

    It 'назва = MPE (в т.ч. іншого регістру) або порожня -> просто MPE' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/hse401/Hse401Nae.Lib.ps1')
        $dash = [string][char]0x2014
        if ((Format-Hse401NaeDisplay -Mpe '0081' -Name '0081') -cne '0081') { throw 'назва = MPE' }
        if ((Format-Hse401NaeDisplay -Mpe '3635_POD' -Name '3635_pod') -cne '3635_POD') { throw 'назва = MPE без урахування регістру' }
        if ((Format-Hse401NaeDisplay -Mpe '0081' -Name '') -cne '0081') { throw 'порожня назва' }
        if ((Format-Hse401NaeDisplay -Mpe '0081' -Name $null) -cne '0081') { throw 'null-назва' }
    }

    It 'повторний прогін не подвоює префікс; префікс іншого регістру нормалізується' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/hse401/Hse401Nae.Lib.ps1')
        $dash = [string][char]0x2014
        $once = Format-Hse401NaeDisplay -Mpe '3635_POD' -Name 'Bolashak station'
        $twice = Format-Hse401NaeDisplay -Mpe '3635_POD' -Name $once
        if ($twice -cne $once) { throw "Подвійний префікс: '$twice'" }
        $lowered = "3635_pod $dash Bolashak station"
        $fixed = Format-Hse401NaeDisplay -Mpe '3635_POD' -Name $lowered
        if ($fixed -cne $once) { throw "Префікс не нормалізовано: '$fixed'" }
        $onlyPrefix = Format-Hse401NaeDisplay -Mpe '0081' -Name "0081 $dash 0081"
        if ($onlyPrefix -cne '0081') { throw "Префікс+MPE має скластися до MPE: '$onlyPrefix'" }
    }

    It 'ConvertTo-NaeCode: цифри < 4 знаків доповнюються нулями, суфіксні коди - як є; код вузла і хвіст' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/hse401/Hse401Nae.Lib.ps1')
        $dash = [string][char]0x2014
        if ((ConvertTo-NaeCode '81') -cne '0081') { throw '81' }
        if ((ConvertTo-NaeCode ' 3635_POD ') -cne '3635_POD') { throw 'суфікс' }
        if ((Get-Hse401NaeSrcCode 'Atyrau' 'Offshore' '0081') -cne 'N_ATYRAU_OFFSHORE_0081') { throw 'код вузла' }
        if ((Get-Hse401MpeFromNaeCode 'N_ATYRAU_OFFSHORE_0081') -cne '0081') { throw 'хвіст' }
        if ((Get-Hse401MpeFromNaeCode 'N_MANGYSTAU_ONSHORE_3635_POD') -cne '3635_POD') { throw 'хвіст із суфіксом' }
        if ($null -ne (Get-Hse401MpeFromNaeCode 'R_ATYRAU')) { throw 'не SRC-код' }
    }
}
