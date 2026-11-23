<#
.SYNOPSIS
    ✎ D-571 (P-1123-6, «створи еталони і мігруй»): СИНТЕТИЧНІ еталонні рядки для версій, яких не доводять ні дані live, ні база ECR
    (їхні таблиці старої системи порожні), — з покриттям кожного правила рядків і кожної гілки формул. Результат — cases.json для
    `parity-cases` (очікування рахує СТАРИЙ рушій формул) і coverage.json (покриття правил і умов).
.DESCRIPTION
    На версію (чинна, рушій Formulas, рівень Configuration):
      1. правила рядків (calc.MethodologyRule + «позичені/відсутні власні» з -BorrowedRules) -> рядок на кожне правило: колонка
         правила = значення правила (Lookup — Id запису), псевдонім IDCodeNAEProject -> Land_NAE;
      2. умови формул і правила категорії (`@X op число`, `@X = 'текст'`, `in(@X, ...)`) -> кандидати значень: сам літерал, ±1
         (межа), 0, null (немає комірки); для `in(...)` — кожен елемент і значення поза списком;
      3. базові числа реалістичні (12.5; місяць — 6), рядок-варіація змінює ОДИН аргумент (one-factor-at-a-time);
      4. покриття: кожне правило відібрало ≥ 1 рядок; кожна умова має рядок, де вона істинна, і рядок, де хибна (null — окремо).
    Умова `in(CURRENT_SUBSTANCE(), ...)` покривається циклом речовин (кожна речовина версії рахується на кожному рядку).
.PARAMETER Methodology  Коди методологій (чинні версії).
.PARAMETER BorrowedRules  borrowed-rules.json (правила, що додаються новою версією) — їм теж по рядку.
#>
param(
    [string]$Server = 'localhost',
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string[]]$Methodology,
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string]$CoverageOut,
    [string]$BorrowedRules,
    [long]$DocumentId = 0,
    [int]$PeriodKey = 0,
    [int]$MaxRows = 80
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$Methodology = @($Methodology | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$tgtCs = "Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=True"
. (Join-Path $PSScriptRoot 'MsPhase.Lib.ps1')

function Read-Rows([string]$sql, [hashtable]$params = @{}) {
    $cn = New-Object System.Data.SqlClient.SqlConnection($tgtCs); $cn.Open()
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 300
        foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) }
        $rd = $cmd.ExecuteReader(); $list = New-Object System.Collections.Generic.List[hashtable]
        while ($rd.Read()) { $row = @{}; for ($i = 0; $i -lt $rd.FieldCount; $i++) { if (-not $rd.IsDBNull($i)) { $row[$rd.GetName($i)] = $rd.GetValue($i) } }; $list.Add($row) }
        $rd.Close(); return , $list
    } finally { $cn.Close() }
}

if ($DocumentId -le 0 -or $PeriodKey -le 0) {
    $ctx = Read-Rows 'SELECT TOP 1 i.DocumentId, i.PeriodKey FROM doc.TableInstance i JOIN cfg.CalculationBinding b ON b.TableDefId = i.TableDefId AND b.IsActive = 1 WHERE i.SheetCopyId IS NULL ORDER BY i.PeriodKey, i.DocumentId'
    $DocumentId = [long]$ctx[0]['DocumentId']; $PeriodKey = [int]$ctx[0]['PeriodKey']
}
$extra = @{}
if ($BorrowedRules -and (Test-Path -LiteralPath $BorrowedRules)) {
    foreach ($v in (Get-Content -LiteralPath $BorrowedRules -Raw -Encoding UTF8 | ConvertFrom-Json).versions) { $extra[[string]$v.methodologyCode] = @($v.rules) }
}

$versions = New-Object System.Collections.Generic.List[object]
$coverage = New-Object System.Collections.Generic.List[object]
foreach ($m in $Methodology) {
    $vr = Read-Rows "SELECT TOP 1 v.Id, v.Version FROM calc.MethodologyVersion v JOIN calc.Methodology m ON m.Id = v.MethodologyId WHERE m.Code = @m AND v.Status = 1 AND v.Engine = 0 AND v.Level = 1 ORDER BY v.EffectiveFrom DESC, v.Id DESC" @{ '@m' = $m }
    if ($vr.Count -eq 0) { Write-Host "${m}: немає чинної формульної версії рівня Configuration"; continue }
    $vid = [int]$vr[0]['Id']
    $texts = New-Object System.Collections.Generic.List[string]
    $args1 = New-Object System.Collections.Generic.HashSet[string]
    foreach ($f in (Read-Rows 'SELECT Expression, ArgumentsCsv FROM calc.MethodologyFormula WHERE MethodologyVersionId = @v' @{ '@v' = $vid })) {
        $texts.Add([string]$f['Expression'])
        foreach ($a in ([string]$f['ArgumentsCsv'] -split '[;,]')) { $n = $a.Trim(); if ($n.StartsWith('@')) { [void]$args1.Add($n.Substring(1).TrimEnd('?')) } }
    }
    foreach ($cr in (Read-Rows 'SELECT Expression FROM calc.CategoryRule WHERE MethodologyVersionId = @v' @{ '@v' = $vid })) {
        $texts.Add([string]$cr['Expression'])
        foreach ($mm in [regex]::Matches([string]$cr['Expression'], '@([A-Za-z_][A-Za-z0-9_]*)')) { [void]$args1.Add($mm.Groups[1].Value) }
    }
    $conds = @(Get-MsBranchConditions -Expressions @($texts.ToArray()))

    # Правила: живі + ті, що додасть нова версія.
    $ruleList = New-Object System.Collections.Generic.List[object]
    foreach ($r in (Read-Rows 'SELECT Code, MatchJson FROM calc.MethodologyRule WHERE MethodologyVersionId = @v AND IsActive = 1' @{ '@v' = $vid })) { $ruleList.Add([pscustomobject]@{ Code = [string]$r['Code']; MatchJson = [string]$r['MatchJson'] }) }
    if ($extra.ContainsKey($m)) { foreach ($r in $extra[$m]) { $ruleList.Add([pscustomobject]@{ Code = [string]$r.code; MatchJson = [string]$r.matchJson }) } }
    $ruleArgs = New-Object System.Collections.Generic.List[object]
    foreach ($r in $ruleList) {
        $set = [ordered]@{}
        foreach ($p in ($r.MatchJson | ConvertFrom-Json).PSObject.Properties) {
            $col = Read-Rows 'SELECT Code, DataType FROM cfg.ColumnDef WHERE Id = @c' @{ '@c' = [int]$p.Name }
            if ($col.Count -eq 0) { continue }
            $code = [string]$col[0]['Code']
            $val = if ([int]$col[0]['DataType'] -eq 5) { @{ entryId = [long]$p.Value } } else { @{ valueString = [string]$p.Value } }
            $set[$code] = $val
            if ($code -eq 'IDCodeNAEProject') { $set['Land_NAE'] = $val }
        }
        $ruleArgs.Add([pscustomobject]@{ Code = $r.Code; Args = $set })
    }
    if ($ruleArgs.Count -eq 0) { $ruleArgs.Add([pscustomobject]@{ Code = '(без правил)'; Args = [ordered]@{} }) }

    $rows = @(New-MsSyntheticRows -Arguments @($args1) -Conditions $conds -RuleArgs @($ruleArgs.ToArray()) -MaxRows $MaxRows)
    $cov = Get-MsBranchCoverage -Conditions $conds -Rows $rows -RuleCodes @($ruleArgs | ForEach-Object { $_.Code })
    $coverage.Add([ordered]@{ methodologyCode = $m; versionId = $vid; rows = $rows.Count; rules = $cov.Rules; conditions = $cov.Conditions; uncovered = $cov.Uncovered })
    Write-Host ("{0} {1}: рядків {2}, правил {3}/{4}, умов {5}/{6} покрито" -f $m, $vr[0]['Version'], $rows.Count, @($cov.Rules | Where-Object { $_.rows -gt 0 }).Count, @($cov.Rules).Count, @($cov.Conditions | Where-Object { $_.covered }).Count, @($cov.Conditions).Count)
    $cases = @($rows | ForEach-Object {
            [ordered]@{ key = $_.Key; table = 'SYN'; arguments = @($_.Args.GetEnumerator() | Where-Object { $null -ne $_.Value } | ForEach-Object {
                        $a = $_.Value
                        [ordered]@{ argumentCode = $_.Key; value = $(if ($a.ContainsKey('value')) { $a.value } else { $null }); valueString = $(if ($a.ContainsKey('valueString')) { $a.valueString } else { $null }); entryId = $(if ($a.ContainsKey('entryId')) { $a.entryId } else { $null }) }
                    }) }
        })
    $versions.Add([ordered]@{ versionId = $vid; methodologyCode = $m; version = [string]$vr[0]['Version']; cases = $cases })
}
foreach ($pair in @(@($Out, [ordered]@{ format = 'ecr-ms-reference-cases'; formatVersion = 1; referenceDatabase = 'synthetic'; documentId = $DocumentId; periodKey = $PeriodKey; versions = @($versions.ToArray()) }),
        @($CoverageOut, [ordered]@{ format = 'ecr-ms-branch-coverage'; formatVersion = 1; versions = @($coverage.ToArray()) }))) {
    $dir = Split-Path -Parent ([IO.Path]::GetFullPath($pair[0])); if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($pair[0]), ($pair[1] | ConvertTo-Json -Depth 12), (New-Object Text.UTF8Encoding($false)))
}
$bad = @($coverage | Where-Object { @($_.uncovered).Count -gt 0 })
if ($bad.Count -gt 0) { Write-Host ("НЕПОКРИТО: " + (($bad | ForEach-Object { "$($_.methodologyCode): $(@($_.uncovered) -join '; ')" }) -join ' | ')); exit 1 }
