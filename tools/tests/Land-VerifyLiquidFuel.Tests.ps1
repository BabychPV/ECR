<#
    Pester (3/5 сумісний синтаксис без `Should`): tools/land/Verify-LandTemplate.ps1, B107 (F13 AU з B98) - мости T3_00
    порівнюють НАЗВУ палива з назвами записів 'liquidFuels' моделі; перейменування такого запису на стенді дає видиме
    #N/A у рідких рядках. Verify звіряє поле NAME живих записів із моделлю. Мережі немає: функція береться з AST.
#>

Describe 'Verify-LandTemplate: назви рідкого палива T3_00 (B107)' {
    It 'розбіжність назви, відсутній запис і збіг - чиста функція без мережі' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools/land/Verify-LandTemplate.ps1'), [ref]$null, [ref]$null)
        $def = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-LiquidFuelNameMismatch' }, $true)
        if ($null -eq $def) { throw 'немає Get-LiquidFuelNameMismatch: перейменування рідкого палива на стенді лишилося б непоміченим до #N/A у документах' }
        . ([scriptblock]::Create($def.Extent.Text))

        $model = @(
            [pscustomobject]@{ code = 'DIESEL'; name = 'Diesel - Дизель' },
            [pscustomobject]@{ code = 'GASOLINE'; name = 'Gasoline - Бензин' },
            [pscustomobject]@{ code = 'KEROSENEJETFUEL'; name = 'Kerosene (Jet fuel) - Керосин' })
        $codes = @('DIESEL', 'GASOLINE', 'KEROSENEJETFUEL')

        # збіг (і зайві пробіли по краях не рахуються розбіжністю)
        $ok = Get-LiquidFuelNameMismatch -Registry 'R' -Codes $codes -ModelEntries $model -LiveNames @{
            DIESEL = 'Diesel - Дизель'; GASOLINE = ' Gasoline - Бензин '; KEROSENEJETFUEL = 'Kerosene (Jet fuel) - Керосин' }
        if (@($ok).Count -ne 0) { throw ('зайва розбіжність: ' + (@($ok) -join ' | ')) }

        # перейменовано + запису немає
        $m = Get-LiquidFuelNameMismatch -Registry 'LAND_STATIONARY_TYPE_OF_FUEL' -Codes $codes -ModelEntries $model -LiveNames @{
            DIESEL = 'Diesel fuel'; GASOLINE = 'Gasoline - Бензин'; KEROSENEJETFUEL = $null }
        if (@($m).Count -ne 2) { throw "очікувалось 2 розбіжності, отримано $(@($m).Count): $(@($m) -join ' | ')" }
        if ((@($m) -join ' | ') -notlike "*DIESEL NAME 'Diesel fuel' != 'Diesel - Дизель'*") { throw "немає розбіжності назви DIESEL: $(@($m) -join ' | ')" }
        if ((@($m) -join ' | ') -notlike '*немає чинного запису KEROSENEJETFUEL*') { throw "немає відсутнього запису: $(@($m) -join ' | ')" }
    }

    It 'перевірка викликається на живих записах і її розбіжності потрапляють у $bad (код виходу 1)' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        $text = [IO.File]::ReadAllText((Join-Path $repo 'tools/land/Verify-LandTemplate.ps1'))
        if ($text -notmatch '\$liqBad = Get-LiquidFuelNameMismatch') { throw 'Get-LiquidFuelNameMismatch не викликається' }
        if ($text -notmatch '\$bad \+= @\(\$liqBad\)') { throw 'розбіжності назв не йдуть у $bad' }
        if ($text -notmatch 'existingInputBridges\.T3_00') { throw 'перевірка не читає notApplicable T3_00 з моделі' }
    }
}
