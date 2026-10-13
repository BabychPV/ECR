<#
.SYNOPSIS
    Чисті функції порівняння моделі Land зі структурою версії (без мережі й SQL): спільні для
    Build-LandTemplate.ps1 (клон-чернетка) і Verify-LandTemplate.ps1.

.DESCRIPTION
    D-YE-1: формули колонок. Build раніше лише ВСТАВЛЯВ відсутні формули; вираз наявної колонки клону
    лишався старим (T5_08/T5_09 Winter `< 4 OR > 9` замість `< 3 OR > 8`). Тепер план порівнює вираз
    моделі з поточним і повертає окремо вставки та оновлення.
    LP-28: таблиці версії, яких немає в моделі (застарілі T9a_03/06/07 на клоні).
#>

# Ключ порівняння виразу: сервер зберігає вираз дослівно (FormulaDef.SetExpression), тож нормалізуємо лише
# крайні пробіли й послідовності пробільних символів (у т.ч. переноси рядків).
function ConvertTo-LandFormulaKey([string]$Expression) {
    if ($null -eq $Expression) { return '' }
    return ([regex]::Replace($Expression, '\s+', ' ')).Trim()
}

# Wanted: масив { Key; Expression } (Key = "tableId/columnId"); Existing: hashtable Key -> { Id; Expression; Dialect }.
# Повертає @{ Insert = @(Wanted...); Update = @({ Id; Key; Expression }...); Same = n }.
# Діалект формули колонки шаблону - Template (0): інший діалект теж виправляється оновленням.
function Get-LandFormulaPlan($Wanted, [hashtable]$Existing) {
    $ins = @(); $upd = @(); $same = 0
    foreach ($w in @($Wanted)) {
        if ($null -eq $w) { continue }
        if (-not $Existing.ContainsKey([string]$w.Key)) { $ins += $w; continue }
        $e = $Existing[[string]$w.Key]
        $dialectOk = ([int]$e.Dialect -eq 0)
        if ($dialectOk -and (ConvertTo-LandFormulaKey ([string]$e.Expression)) -ceq (ConvertTo-LandFormulaKey ([string]$w.Expression))) { $same++; continue }
        $upd += [pscustomobject]@{ Id = [int]$e.Id; Key = [string]$w.Key; Expression = [string]$w.Expression }
    }
    return @{ Insert = $ins; Update = $upd; Same = $same }
}

# Кодові назви "SHEET/TABLE" таблиць структури, яких немає в моделі (за винятком -Known).
# Структура - відповідь GET template-versions/{id}/structure (sheets[].tables[]), Mdl - land-template.model.json.
function Get-LandStaleTables($Structure, $Mdl, [string[]]$Known = @()) {
    $want = @{}
    foreach ($sh in @($Mdl.sheets)) { foreach ($tb in @($sh.tables)) { $want["$($sh.code)/$($tb.code)"] = $true } }
    $stale = @()
    foreach ($s in @($Structure.sheets)) {
        foreach ($t in @($s.tables)) {
            $k = "$($s.code)/$($t.code)"
            if ($want.ContainsKey($k)) { continue }
            if (@($Known) -contains $k -or @($Known) -contains [string]$t.code) { continue }
            $stale += [pscustomobject]@{ Key = $k; Sheet = [string]$s.code; Table = [string]$t.code }
        }
    }
    return @($stale)
}

# Розбіжності формул колонок і readOnly між моделлю та структурою версії. Повертає рядки виду
# "SHEET/TABLE: формула відрізняється: C1,C2" (значень виразів не друкує).
function Get-LandFormulaMismatches($Structure, $Mdl) {
    $byTable = @{}
    foreach ($s in @($Structure.sheets)) { foreach ($t in @($s.tables)) { $byTable["$($s.code)/$($t.code)"] = $t } }
    $out = @()
    foreach ($sh in @($Mdl.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $etb = $byTable["$($sh.code)/$($tb.code)"]
            if ($null -eq $etb) { continue }
            $diff = @(); $noFormula = @(); $dialect = @(); $ro = @()
            foreach ($c in @($tb.columns)) {
                $ecol = @($etb.columns) | Where-Object { [string]$_.code -eq [string]$c.code } | Select-Object -First 1
                if ($null -eq $ecol) { continue }
                if ($c.expression) {
                    if ([string]::IsNullOrEmpty([string]$ecol.formulaExpression)) { $noFormula += [string]$c.code }
                    elseif ((ConvertTo-LandFormulaKey ([string]$ecol.formulaExpression)) -cne (ConvertTo-LandFormulaKey ([string]$c.expression))) { $diff += [string]$c.code }
                    elseif ($null -ne $ecol.formulaDialect -and -not ([string]$ecol.formulaDialect -in 'Template', '0')) { $dialect += [string]$c.code }
                }
                $wantRo = [bool]($c.readOnly -or [string]$c.dataType -in 'Formula', 'Calculated')
                if ($wantRo -and -not [bool]$ecol.isReadOnly) { $ro += [string]$c.code }
            }
            $k = "$($sh.code)/$($tb.code)"
            if ($diff.Count) { $out += "${k}: формула відрізняється: $($diff -join ',')" }
            if ($noFormula.Count) { $out += "${k}: формули немає: $($noFormula -join ',')" }
            if ($dialect.Count) { $out += "${k}: діалект формули не Template: $($dialect -join ',')" }
            if ($ro.Count) { $out += "${k}: колонка має бути лише для читання: $($ro -join ',')" }
        }
    }
    return @($out)
}

# D-UPG-1: стеля динамічних рядків (TableDef.MaxDynamicRows) НАЯВНОЇ таблиці клону. Build раніше лише ВСТАВЛЯВ нові
# таблиці зі стелею моделі, а в наявних лишав стару (T3_00=498, T9_04=184, T9_07=100) - сервер відхиляв рядки понад
# стелю (ECR-ROW-0409). Правило - лише РОЗШИРЕННЯ: стеля піднімається, коли в моделі вона БІЛЬША; нижча чи рівна, а також
# «без стелі» (NULL) у клоні не чіпаються (зменшення стелі могло б урізати дані документів).
# Wanted: масив { Key; MaxRows } (Key = "SHEET/TABLE"; MaxRows - maxRows моделі, 0/null = без вимоги);
# Existing: hashtable Key -> { Id; MaxRows } (MaxRows = $null, якщо стелі немає).
# Повертає @{ Raise = @({ Id; Key; From; To }...); Same = n }.
function Get-LandMaxRowsPlan($Wanted, [hashtable]$Existing) {
    $raise = @(); $same = 0
    foreach ($w in @($Wanted)) {
        if ($null -eq $w -or -not $w.MaxRows) { continue }
        if (-not $Existing.ContainsKey([string]$w.Key)) { continue }
        $e = $Existing[[string]$w.Key]
        if ($null -eq $e.MaxRows) { $same++; continue }
        if ([int]$e.MaxRows -lt [int]$w.MaxRows) {
            $raise += [pscustomobject]@{ Id = [int]$e.Id; Key = [string]$w.Key; From = [int]$e.MaxRows; To = [int]$w.MaxRows }
        }
        else { $same++ }
    }
    return @{ Raise = $raise; Same = $same }
}

# Розбіжності стелі динамічних рядків між структурою версії (tables[].maxDynamicRows) і моделлю (tables[].maxRows):
# рядки виду "SHEET/TABLE: стеля динамічних рядків було -> має бути". Розбіжність - лише стеля НИЖЧА за модель
# (стеля вища за модель чи відсутня - розширення, Build її не зменшує). Значень документів тут немає.
function Get-LandMaxRowsMismatches($Structure, $Mdl) {
    $byTable = @{}
    foreach ($s in @($Structure.sheets)) { foreach ($t in @($s.tables)) { $byTable["$($s.code)/$($t.code)"] = $t } }
    $out = @()
    foreach ($sh in @($Mdl.sheets)) {
        foreach ($tb in @($sh.tables)) {
            if (-not $tb.maxRows) { continue }
            $etb = $byTable["$($sh.code)/$($tb.code)"]
            if ($null -eq $etb -or $null -eq $etb.maxDynamicRows) { continue }
            if ([int]$etb.maxDynamicRows -lt [int]$tb.maxRows) {
                $out += ("{0}/{1}: стеля динамічних рядків {2} -> {3}" -f $sh.code, $tb.code, [int]$etb.maxDynamicRows, [int]$tb.maxRows)
            }
        }
    }
    return @($out)
}
