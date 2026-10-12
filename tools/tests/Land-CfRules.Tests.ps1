<#
    Pester (3/5 сумісний синтаксис без `Should`): tools/land/Land-CfRules.ps1 — перетворення conditionalFormatsRowScoped моделі Land
    у правила PUT .../conditional-formats за контрактом L6 (lane cellstyle-l6-3). Мережі й ECR не потрібно.
    Запуск: Invoke-Pester -Path tools/tests. Кожен It самодостатній (у Pester 5 змінні Describe не доходять до It).
#>

Describe 'Land-CfRules: conditionalFormatsRowScoped -> правила умовного форматування' {
    It 'числові баланс-правила -> row-scope з rowKeys, текстові Removed -> textEq на колонці таблиці' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/Land-CfRules.ps1')
        $model = Get-Content -LiteralPath (Join-Path $repo 'docs/delivery/reference-data/land-template/land-template.model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $rules = @(ConvertTo-LandCfRules $model)

        if ($rules.Count -lt 100) { throw "Правил лише $($rules.Count)" }
        if ($rules.Count -gt 5000) { throw "Правил $($rules.Count) > 5000 (стеля SaveConditionalFormatsHandler.MaxRules)" }

        $ne = @($rules | Where-Object { $_.operator -eq 'ne' -and $_.scope -eq 'row' -and $_.tableCode -eq 'T8_00' })
        $eq = @($rules | Where-Object { $_.operator -eq 'eq' -and $_.scope -eq 'row' -and $_.tableCode -eq 'T8_00' })
        if ($ne.Count -eq 0 -or $eq.Count -eq 0) { throw 'Немає правил балансу T8_00 (ne/eq)' }
        if (@($ne | Where-Object { $_.backgroundHex -ne '#FF0000' -or $_.value -ne '0' }).Count -ne 0) { throw 'ne 0 мусить бути #FF0000' }
        if (@($eq | Where-Object { $_.backgroundHex -ne '#00FF00' -or $_.value -ne '0' }).Count -ne 0) { throw 'eq 0 мусить бути #00FF00' }

        $text = @($rules | Where-Object { $_.operator -eq 'textEq' })
        if ($text.Count -eq 0) { throw 'Немає textEq' }
        $bad = @($text | Where-Object { $_.value -ne 'Removed' -or $_.foregroundHex -ne '#FF0000' -or -not $_.isBold -or $_.scope -or -not $_.tableCode -or $_.rowKeys })
        if ($bad.Count -ne 0) { throw 'textEq Removed: червоний жирний на колонці таблиці без rowKeys' }
    }

    It 'кожне правило відповідає інваріантам ConditionalFormatRule.Validate (область, ключі, кольори, унікальність)' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/Land-CfRules.ps1')
        $model = Get-Content -LiteralPath (Join-Path $repo 'docs/delivery/reference-data/land-template/land-template.model.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $rules = @(ConvertTo-LandCfRules $model)

        # колонки й рядки існують у моделі
        $cols = @{}; $rowKeys = @{}
        foreach ($sh in @($model.sheets)) { foreach ($tb in @($sh.tables)) {
                $cols[[string]$tb.code] = @($tb.columns | ForEach-Object { [string]$_.code })
                $rowKeys[[string]$tb.code] = @($tb.rows | ForEach-Object { [string]$_.key })
            } }
        $seen = @{}
        foreach ($r in $rules) {
            if ($r.operator -notin 'eq', 'ne', 'textEq', 'textNe', 'gt', 'ge', 'lt', 'le') { throw "оператор $($r.operator)" }
            if (-not $r.tableCode -or -not $cols.ContainsKey($r.tableCode)) { throw "таблиця $($r.tableCode)" }
            if ($cols[$r.tableCode] -notcontains $r.columnCode) { throw "колонки $($r.columnCode) немає в $($r.tableCode)" }
            foreach ($hex in @($r.backgroundHex, $r.foregroundHex)) { if ($hex -and $hex -notmatch '^#[0-9a-fA-F]{6}$') { throw "колір $hex" } }
            if ($r.operator -in 'eq', 'ne', 'gt', 'ge', 'lt', 'le') { $d = [decimal]0; if (-not [decimal]::TryParse([string]$r.value, [Globalization.NumberStyles]::Number, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { throw "операнд $($r.value)" } }
            if ($r.scope -eq 'row') {
                $k = @($r.rowKeys)
                if ($k.Count -eq 0 -or $k.Count -gt 500) { throw "rowKeys $($k.Count)" }
                if (@($k | Select-Object -Unique).Count -ne $k.Count) { throw 'rowKeys повторюються' }
                foreach ($x in $k) { if ($rowKeys[$r.tableCode] -notcontains $x) { throw "рядка $x немає в $($r.tableCode)" } }
            }
            elseif ($null -ne $r.rowKeys -or $r.scope) { throw 'rowKeys/scope лише разом' }
            $key = "$($r.tableCode)|$($r.columnCode)|$($r.operator)|$($r.value)|$($r.scope)|$(@($r.rowKeys) -join ',')"
            if ($seen.ContainsKey($key)) { throw "дубль правила $key" }
            $seen[$key] = $true
        }
    }

    It 'правило без rowKeys у моделі лишається колонковим, але прив''язаним до таблиці; невідомий оператор відкидається' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/Land-CfRules.ps1')
        $m = [pscustomobject]@{ conditionalFormatsRowScoped = @(
                [pscustomobject]@{ table = 'TX'; columns = @('A', 'B'); operator = 'gt'; value = '5'; valueKind = 'text'; backgroundHex = $null; foregroundHex = '#FF0000'; isBold = $false; rowKeys = $null },
                [pscustomobject]@{ table = 'TX'; columns = @('A'); operator = 'eq'; value = '0'; valueKind = 'number'; backgroundHex = '#00FF00'; foregroundHex = $null; isBold = $false; rowKeys = @('R1', 'R2') },
                [pscustomobject]@{ table = 'TX'; columns = @('A'); operator = 'eq'; value = '0'; valueKind = 'number'; backgroundHex = '#00FF00'; foregroundHex = $null; isBold = $false; rowKeys = @('R2', 'R3') }) }
        $r = @(ConvertTo-LandCfRules $m)
        if ($r.Count -ne 1) { throw "очікувалось 1 правило, є $($r.Count): текстовий gt відкидається, два row-правила зливаються" }
        if (@($r[0].rowKeys).Count -ne 3 -or $r[0].scope -ne 'row' -or $r[0].columnCode -ne 'A') { throw 'злиття rowKeys: R1,R2,R3' }
    }
}