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

    It 'модель Land (B26 x B175): T7b_00 S1_6/S2_7 -> {"Land_Status":"RUNNING"} для SQL і API, S3_9 -> нічого' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/Land-RowCellDefaults.ps1')
        $model = Get-Content -LiteralPath (Join-Path $repo 'docs/delivery/reference-data/land-template/land-template.model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $t7b = $null
        foreach ($sh in @($model.sheets)) { foreach ($tb in @($sh.tables)) { if ([string]$tb.code -eq 'T7b_00') { $t7b = $tb } } }
        if ($null -eq $t7b) { throw 'T7b_00 not in model' }
        $byKey = @{}; foreach ($rw in @($t7b.rows)) { $byKey[[string]$rw.key] = $rw }
        foreach ($k in 'S1_6', 'S2_7') {
            $json = ConvertTo-LandRowCellDefaultsJson $byKey[$k]
            if ($json -ne '{"Land_Status":"RUNNING"}') { throw "$k SQL JSON: $json" }
            $api = Get-LandRowCellDefaults $byKey[$k]
            if ($null -eq $api -or $api['Land_Status'] -ne 'RUNNING' -or @($api.Keys).Count -ne 1) { throw "$k API map wrong" }
        }
        if ($null -ne (ConvertTo-LandRowCellDefaultsJson $byKey['S3_9'])) { throw 'S3_9 got JSON' }
        if ($null -ne (Get-LandRowCellDefaults $byKey['S3_9'])) { throw 'S3_9 got API map' }
    }

    It 'Build-LandTemplate передає дефолти рядка обома шляхами: SQL (CellDefaultsJson) і API (cellDefaults у PUT rows)' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        $src = Get-Content -LiteralPath (Join-Path $repo 'tools/land/Build-LandTemplate.ps1') -Raw -Encoding UTF8
        foreach ($needle in '. (Join-Path $here ''Land-RowCellDefaults.ps1'')', '$cdJson = ConvertTo-LandRowCellDefaultsJson $rw',
                            '$row[''CellDefaultsJson''] =', 'UPDATE r SET CellDefaultsJson = d.Json',
                            '$rowCellDefaults = Get-LandRowCellDefaults $rw', '$rowBody[''cellDefaults''] = $rowCellDefaults') {
            if (-not $src.Contains($needle)) { throw "Build-LandTemplate.ps1 lacks: $needle" }
        }
    }
}