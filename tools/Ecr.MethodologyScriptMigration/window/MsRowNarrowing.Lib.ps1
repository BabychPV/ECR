<#
.SYNOPSIS
    c11-10 (P-1124-2, ф.2): чиста логіка звуження відбору рядків скрипта методології — прибрати з блоків `rows from` умови, чий
    рядок правило категорії ТОГО Ж скрипта завжди відкидає («категорію не визначено»).
.DESCRIPTION
    W15z (D-571 г) додав ECW_C11_10 V4 28 «позичених» правил C11_01/02/04/05 (Build-MsBorrowedRules.ps1: рядки, на які стара система
    писала записи результатів ECW_C11_10). Але NAE цих рядків (9_04: 0640–0645, 6640–6657; 9_07: 0880–0883) немає в жодній групі
    старого коду (`ECW_C11_10.cs`, typeCalculationGroup: тип не визначено -> «Error formula», жодного tons/gsec) і в переліку категорій
    скрипта: `var Category = …: key(row.Land_NAE)` -> сам NAE, а `reject when … !(Category in (…))` його відкидає. Тобто кожен такий
    рядок дає лише CategoryRuleRejected і жодного значення.

    Умова відбору вважається недосяжною, коли в ній є `row.IDCodeNAEProject == "X"`, а X (без пробілів) — не літерал виразу
    `var Category` (групи NAE) і не в переліку `reject when … Category in (…)`. Тоді категорія рядка = X або "" — і обидва відкидаються.
    Що робить функція з блоком, з якого прибрано всі умови: ВІДМОВА (блок без `where` відбирав би всю таблицю).
#>

function Get-MsScriptCategoryFacts {
    <# Літерали виразу `var Category = …;` і перелік дозволених категорій з `reject when Category == null || Category == "" || !(Category in (…))`. #>
    param([Parameter(Mandatory)][string]$ScriptText)
    $cat = [regex]::Match($ScriptText, '(?m)^var Category = (?<e>.*);\s*$')
    if (-not $cat.Success) { throw 'script has no top-level `var Category = …;` line' }
    $rej = [regex]::Match($ScriptText, 'reject when Category == null \|\| Category == ""\s*\|\| !\(Category in \((?<l>[^)]*)\)\);')
    if (-not $rej.Success) { throw 'script has no `reject when Category == null || Category == "" || !(Category in (…));`' }
    if ($cat.Groups['e'].Value -notmatch ':\s*key\(row\.Land_NAE\)\)\s*:\s*""\)$') { throw 'Category expression does not fall back to key(row.Land_NAE) / "" (unknown shape)' }
    $literals = @([regex]::Matches($cat.Groups['e'].Value, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value -replace '\s', '' })
    $allowed = @([regex]::Matches($rej.Groups['l'].Value, '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value })
    return [pscustomobject]@{ Literals = $literals; Allowed = $allowed }
}

function Get-MsFormulaCategoryFacts {
    <#
    .SYNOPSIS  Те саме для ФОРМУЛЬНОЇ версії: літерали правила категорії (calc.CategoryRule.Expression, рядки в '…') і перелік
               категорій версії (calc.MethodologyCategory). Правила категорії немає — $null (версія нічого не відкидає за категорією).
    #>
    param([string]$Expression, [string[]]$Categories = @())
    if ([string]::IsNullOrWhiteSpace($Expression)) { return $null }
    $literals = @([regex]::Matches($Expression, "'([^']*)'") | ForEach-Object { $_.Groups[1].Value -replace '\s', '' })
    return [pscustomobject]@{ Literals = $literals; Allowed = @($Categories | Where-Object { $_ }) }
}

function Select-MsBorrowableRules {
    <#
    .SYNOPSIS  c11-10: чуже правило старої системи позичається методології M, лише коли M може ВИЗНАЧИТИ КАТЕГОРІЮ його рядків.
    .DESCRIPTION
        W15z позичив ECW_C11_10 28 правил, чиї NAE (0640–0645, 6640–6657, 0880–0883) правило категорії C11_10 завжди відкидає:
        стара система писала на ці рядки записи C11_10 без жодного значення (тип не визначено), і нова версія дала лише
        CategoryRuleRejected. Умова NAE правила (ключ Land_NAE / IDCodeNAEProject) мусить бути серед літералів виразу категорії
        або в переліку категорій. Правило без умови NAE чи версія без правила категорії ($Facts = $null) — не фільтруються.
    .OUTPUTS  {Kept = правила; Rejected = правила з NAE поза категоріями}.
    #>
    param([object[]]$Rules = @(), $Facts)
    $kept = New-Object System.Collections.Generic.List[object]; $rejected = New-Object System.Collections.Generic.List[object]
    foreach ($r in @($Rules)) {
        $naeKey = @($r.Conditions.Keys | Where-Object { $_ -match '^(Land_NAE|IDCodeNAEProject)$' }) | Select-Object -First 1
        if ($null -eq $Facts -or $null -eq $naeKey) { $kept.Add($r); continue }
        $nae = ([string]$r.Conditions[$naeKey]) -replace '\s', ''
        if ($Facts.Literals -contains $nae -or $Facts.Allowed -contains $nae) { $kept.Add($r) } else { $rejected.Add($r) }
    }
    return [pscustomobject]@{ Kept = @($kept.ToArray()); Rejected = @($rejected.ToArray()) }
}

function Get-MsRulesOfRemovedClauses {
    <#
    .SYNOPSIS  c11-10: правила версії (calc.MethodologyRule, предикат «Id колонки -> значення»), чий НАБІР ЗНАЧЕНЬ збігається з
               набором літералів знятої умови, — їх стадія вимикає в чернетці, щоб підказки, покриття й вимоги входів не вважали
               ці рядки рядками методології. Активні правила лише; повертає коди.
    #>
    param([object[]]$Rules = @(), [object[]]$Removed = @())
    $sets = @{}
    foreach ($x in @($Removed)) {
        $values = @([regex]::Matches([string]$x.Clause, '==\s*"([^"]*)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object)
        if ($values.Count -gt 0) { $sets[($values -join [char]1)] = 1 }
    }
    $codes = New-Object System.Collections.Generic.List[string]
    foreach ($r in @($Rules)) {
        if (-not $r.isActive) { continue }
        $values = @(([string]$r.matchJson | ConvertFrom-Json).PSObject.Properties | ForEach-Object { [string]$_.Value } | Sort-Object)
        if ($values.Count -gt 0 -and $sets.ContainsKey($values -join [char]1)) { $codes.Add([string]$r.code) }
    }
    return @($codes.ToArray())
}

function Remove-MsUnreachableRowClauses {
    <#
    .SYNOPSIS  Повертає {Text; Removed = @({Table; Nae; Clause})}. Заголовок «// Methodology M, version Vn» / «// Effective from …»
               переписується на -NewVersion / -EffectiveFrom, якщо їх задано.
    #>
    param([Parameter(Mandatory)][string]$ScriptText, [string]$NewVersion, [string]$EffectiveFrom, [string]$Note)
    $facts = Get-MsScriptCategoryFacts -ScriptText $ScriptText
    $nl = if ($ScriptText.Contains("`r`n")) { "`r`n" } else { "`n" }
    $lines = $ScriptText -split '\r?\n'
    $out = New-Object System.Collections.Generic.List[string]
    $removed = New-Object System.Collections.Generic.List[object]
    $table = $null; $kept = 0; $pendingWhere = $false; $blockClauses = 0
    foreach ($line in $lines) {
        $m = [regex]::Match($line, '^rows from (?<t>\S+)\s*$')
        if ($m.Success) { $table = $m.Groups['t'].Value; $kept = 0; $blockClauses = 0; $pendingWhere = $false; $out.Add($line); continue }
        $c = [regex]::Match($line, '^(?<ind>\s*)(?<op>where|\|\|)\s+\((?<cond>.*)\)\s*$')
        if ($null -ne $table -and $c.Success) {
            $blockClauses++
            $n = [regex]::Match($c.Groups['cond'].Value, 'row\.IDCodeNAEProject == "(?<x>[^"]*)"')
            $x = if ($n.Success) { $n.Groups['x'].Value -replace '\s', '' } else { $null }
            if ($null -ne $x -and $facts.Literals -notcontains $x -and $facts.Allowed -notcontains $x) {
                $removed.Add([pscustomobject]@{ Table = $table; Nae = $n.Groups['x'].Value; Clause = $c.Groups['cond'].Value })
                if ($c.Groups['op'].Value -eq 'where') { $pendingWhere = $true }
                continue
            }
            if ($pendingWhere) { $line = '    where (' + $c.Groups['cond'].Value + ')'; $pendingWhere = $false }
            $kept++; $out.Add($line); continue
        }
        if ($null -ne $table -and ($line -match '^\s*active when ' -or $line -match '^\s*$') -and $blockClauses -gt 0 -and $kept -eq 0) {
            throw "rows from ${table}: every where-clause is unreachable; removing them would select the whole table (refused)"
        }
        if ($line -match '^\s*$') { $table = $null }
        $out.Add($line)
    }
    if ($NewVersion) { $out[0] = [regex]::Replace($out[0], '^(// Methodology \S+, version )\S+$', '${1}' + $NewVersion) }
    if ($EffectiveFrom) { for ($i = 0; $i -lt [Math]::Min(5, $out.Count); $i++) { if ($out[$i] -match '^// Effective from ') { $out[$i] = '// Effective from ' + $EffectiveFrom } } }
    if ($Note -and $removed.Count -gt 0) {
        $at = 0; while ($at -lt $out.Count -and $out[$at].StartsWith('//')) { $at++ }
        $out.Insert($at, '// ' + $Note)
    }
    return [pscustomobject]@{ Text = ($out -join $nl); Removed = @($removed.ToArray()) }
}
