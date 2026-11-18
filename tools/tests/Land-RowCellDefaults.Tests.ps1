<#
    Pester (3/5 сумісний синтаксис без `Should`): tools/land/Land-RowCellDefaults.ps1 — B175, дефолти комірок рядка моделі
    Land для SQL-шляху (CellDefaultsJson) і API-шляху (поле cellDefaults). Мережі й ECR не потрібно.
    Мутація: Get-LandRowCellDefaults завжди повертає $null — червоніють перший і другий It.
#>

Describe 'Land-RowCellDefaults: rows[].cellDefaults -> CellDefaultsJson / cellDefaults' {
    It 'об''єкт моделі (ConvertFrom-Json) -> впорядкований за кодом JSON; "" лишається порожнім рядком' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/Land-RowCellDefaults.ps1')
        $row = '{"key":"R1","cellDefaults":{"Qty":"","Land_Status":"RUNNING"}}' | ConvertFrom-Json
        $json = ConvertTo-LandRowCellDefaultsJson $row
        if ($json -ne '{"Land_Status":"RUNNING","Qty":""}') { throw "JSON: $json" }
    }

    It 'хеш-таблиця теж приймається, а значення для API — словник код -> текст' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/Land-RowCellDefaults.ps1')
        $map = Get-LandRowCellDefaults @{ key = 'R2'; cellDefaults = @{ B = '2'; A = $null } }
        if ($null -eq $map) { throw 'map is null' }
        if (@($map.Keys) -join ',' -ne 'A,B') { throw "keys: $(@($map.Keys) -join ',')" }
        if ($map['A'] -ne '' -or $map['B'] -ne '2') { throw "values: $($map['A'])/$($map['B'])" }
    }

    It 'рядок без cellDefaults або з порожнім об''єктом -> $null (білдер нічого не шле)' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/Land-RowCellDefaults.ps1')
        $plain = '{"key":"R3"}' | ConvertFrom-Json
        $empty = '{"key":"R4","cellDefaults":{}}' | ConvertFrom-Json
        if ($null -ne (ConvertTo-LandRowCellDefaultsJson $plain)) { throw 'plain row produced JSON' }
        if ($null -ne (Get-LandRowCellDefaults $empty)) { throw 'empty cellDefaults produced map' }
    }
}
