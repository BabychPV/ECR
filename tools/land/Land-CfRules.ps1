<#
.SYNOPSIS
    Перетворює conditionalFormatsRowScoped моделі Land у правила PUT .../conditional-formats (контракт L6 плану стилів,
    lane cellstyle-l6-3: ConditionalFormatRuleDto = columnCode, operator, value, valueTo, backgroundHex, foregroundHex,
    isBold, scope, tableCode, rowKeys, refColumnCode, pattern).

.DESCRIPTION
    Чиста функція без звернень до API (тестується Pester: tools/tests/Land-CfRules.Tests.ps1).
    * числові eq/ne -> eq/ne; текстові eq/ne -> textEq/textNe (значення довідника порівнюється за КОДОМ запису без регістру);
    * правило з rowKeys -> scope = row (+ tableCode, rowKeys <= 500); без rowKeys -> scope колонки (поле відсутнє) + tableCode,
      щоб правило не діяло на колонки з тим самим кодом в інших таблицях версії;
    * правила з однаковими (таблиця, колонка, оператор, значення, кольори) зливаються: rowKeys — об'єднання без повторів, порядок збережено.
#>
$script:LandCfMaxRowKeys = 500
$script:LandCfMaxRules = 5000

function ConvertTo-LandCfRules {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Model)
    $agg = [ordered]@{}
    foreach ($r in @($Model.conditionalFormatsRowScoped)) {
        if ($null -eq $r) { continue }
        $op = [string]$r.operator
        if ([string]$r.valueKind -eq 'text') {
            if ($op -eq 'eq') { $op = 'textEq' } elseif ($op -eq 'ne') { $op = 'textNe' } else { continue }
        }
        elseif ($op -notin 'eq', 'ne', 'gt', 'ge', 'lt', 'le') { continue }
        $isRow = $null -ne $r.rowKeys
        if ([string]::IsNullOrWhiteSpace([string]$r.table)) { continue }
        foreach ($col in @($r.columns)) {
            $key = @([string]$r.table, [string]$col, $op, [string]$r.value, [string]$r.backgroundHex, [string]$r.foregroundHex, [bool]$r.isBold, $isRow) -join '|'
            if (-not $agg.Contains($key)) {
                $agg[$key] = [pscustomobject]@{
                    Table = [string]$r.table; Column = [string]$col; Op = $op; Value = [string]$r.value
                    Bg = $(if ($r.backgroundHex) { [string]$r.backgroundHex } else { $null })
                    Fg = $(if ($r.foregroundHex) { [string]$r.foregroundHex } else { $null })
                    Bold = [bool]$r.isBold; IsRow = $isRow
                    Keys = New-Object 'System.Collections.Generic.List[string]'
                }
            }
            if ($isRow) {
                foreach ($rk in @($r.rowKeys)) { if (-not $agg[$key].Keys.Contains([string]$rk)) { $agg[$key].Keys.Add([string]$rk) } }
            }
        }
    }
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($a in $agg.Values) {
        $chunks = @($null)
        if ($a.IsRow) {
            $chunks = @()
            for ($i = 0; $i -lt $a.Keys.Count; $i += $script:LandCfMaxRowKeys) {
                $chunks += , @($a.Keys.GetRange($i, [Math]::Min($script:LandCfMaxRowKeys, $a.Keys.Count - $i)))
            }
            if ($chunks.Count -eq 0) { continue }
        }
        foreach ($ch in $chunks) {
            $out.Add(@{
                    columnCode = $a.Column; operator = $a.Op; value = $a.Value; valueTo = $null
                    backgroundHex = $a.Bg; foregroundHex = $a.Fg; isBold = $a.Bold
                    scope = $(if ($a.IsRow) { 'row' } else { $null }); tableCode = $a.Table
                    rowKeys = $(if ($a.IsRow) { , @($ch) } else { $null }); refColumnCode = $null; pattern = $null
                })
        }
    }
    # Правила за СТАТУСОМ РЯДКА (expression N($B5="Inactive") в Excel): «усі колонки таблиці» + колонка-джерело (статусна колонка
    # таблиці з довідником) + textEq за КОДОМ запису без регістру. Рішення людини 12.10 17:35Z; джерело - conditionalFormatsRowStatus.
    # Правила на колонці C аркушів 9a/11 (conditionalFormatsStatusUnproven) НЕ переносяться: що в C - не доведено.
    foreach ($s in @($Model.conditionalFormatsRowStatus)) {
        if ($null -eq $s) { continue }
        if ([string]::IsNullOrWhiteSpace([string]$s.table) -or [string]::IsNullOrWhiteSpace([string]$s.statusColumn)) { continue }
        $out.Add(@{
                columnCode = '*'; operator = 'textEq'; value = [string]$s.value; valueTo = $null
                backgroundHex = $(if ($s.backgroundHex) { [string]$s.backgroundHex } else { $null })
                foregroundHex = $(if ($s.foregroundHex) { [string]$s.foregroundHex } else { $null })
                isBold = [bool]$s.isBold
                scope = $null; tableCode = [string]$s.table; rowKeys = $null; refColumnCode = [string]$s.statusColumn; pattern = $null
            })
    }
    return $out.ToArray()
}