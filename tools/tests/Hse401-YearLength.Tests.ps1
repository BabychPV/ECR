# tools/tests/Hse401-YearLength.Tests.ps1
# b14 document-move N1 (D-579, HSE401 v14): таблиця 775 у версії «рік + місячні колонки» - одна копія на рік (якір
# 202601). [Period].Days там - 365 у річному HSE401_2026 і 31 у січні місячного LANDUI2026 (x11.8 після перенесення),
# тому модель рахує довжину року з [Period].Year; оновлювач шаблону переносить зміну формули у нову версію (diff Formula).
# Мутації (локально, 26.11): повернути [Period].Days у $days моделі - червоні перші два тести; порівнювати lc.formulas замість
# ColumnDto.expression (форма до огляду H-1) - червоний третій.
Describe 'HSE401 v14 year length' {
    BeforeAll {
        $root = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $root 'tools/hse401/Hse401Model.Lib.ps1')
        $src = [IO.File]::ReadAllText((Join-Path $root 'tools/hse401/Update-Hse401Template.ps1'), [Text.Encoding]::UTF8)
        $start = $src.IndexOf('function Get-HseL10nText')
        $end = $src.IndexOf('function Get-HseNextVersion')
        $fn = $src.Substring($start, $end - $start)
        . ([scriptblock]::Create($fn))
        $model = Get-Hse401Columns
    }

    It 'жодна формула моделі не читає [Period].Days' {
        $bad = @($model | Where-Object { $_.Expr -and $_.Expr -like '*`[Period`].Days*' })
        if ($bad.Count) { throw ("[Period].Days у формулах: " + (($bad | ForEach-Object { $_.Code }) -join ', ')) }
    }

    It 'дев''ять формул (H_RATE, H_RQ1..4_T/H) ділять на довжину року з [Period].Year' {
        $year = @($model | Where-Object { $_.Expr -and $_.Expr -like '*IF(`[Period`].Year % 4 = 0, 366, 365)*' })
        if ($year.Count -ne 9) { throw "очікувалось 9 формул з довжиною року, є $($year.Count): $(($year | ForEach-Object { $_.Code }) -join ', ')" }
    }

    It 'оновлювач бачить змінену формулу в ColumnDto.expression (справжня форма структури, огляд H-1)' {
        # Форма відповіді GET /template-versions/{id}/structure: TableDto.columns[] = ColumnDto { id, code, expression, ... };
        # окремого списку formulas у TableDto НЕМАЄ - звіряємо зі знімком контракту.
        $snap = [IO.File]::ReadAllText((Join-Path $root 'contracts/openapi.snapshot.json')) | ConvertFrom-Json
        $schemas = $snap.components.schemas
        if (-not $schemas.ColumnDto.properties.PSObject.Properties['expression']) { throw 'ColumnDto без expression - форма змінилась' }
        if ($schemas.TableDto.properties.PSObject.Properties['formulas']) { throw 'TableDto отримав formulas - перегляньте Get-HseTemplateDiff' }

        $rate = @($model | Where-Object { $_.Code -eq 'H_RATE' })[0]
        $i = 0
        $live = @($model | ForEach-Object {
                $i++
                $expr = if ($_.Code -eq 'H_RATE') { '[H_LIMIT_T] / IF([H_BASIS] = NULL, [Period].Days, [H_BASIS])' } elseif ($_.Expr) { $_.Expr } else { $null }
                [pscustomobject]@{ id = $i; code = $_.Code; ordinal = $i; dataType = [string]$_.Type; displayFormat = $_.DisplayFormat
                    expression = $expr; headerL10n = [pscustomobject]@{ en = $_.En; ru = $_.Ru }; isHidden = $false }
            })
        $diff = @(Get-HseTemplateDiff -LiveColumns $live -ModelColumns $model)
        $formula = @($diff | Where-Object { $_.Kind -eq 'Formula' })
        if ($formula.Count -ne 1 -or $formula[0].Code -ne 'H_RATE' -or $formula[0].To -ne $rate.Expr) { throw "diff Formula: $($diff | Out-String)" }

        foreach ($c in $live) { if ($c.code -eq 'H_RATE') { $c.expression = $rate.Expr } }
        if (@(Get-HseTemplateDiff -LiveColumns $live -ModelColumns $model | Where-Object { $_.Kind -eq 'Formula' }).Count) { throw 'v14 вже опубліковано - Formula diff має бути порожнім' }
    }
    It 'оновлювач переносить лише документ HSE401 і бере проєкт з -ProjectCode' {
        if ($src -notmatch '\[string\]\$ProjectCode') { throw 'немає параметра -ProjectCode' }
        if ($src -notmatch "Find-Hse401Document -ProjectCode \`$ProjectCode") { throw 'Find-Hse401Document без -ProjectCode' }
        if ($src -notmatch "\[int\]\`$_.id -eq \[int\]\`$found\['documentId'\]") { throw 'перенос версії не звужено до документа HSE401' }
    }
}
