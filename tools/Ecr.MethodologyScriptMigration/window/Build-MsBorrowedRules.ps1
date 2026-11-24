<#
.SYNOPSIS
    ✎ D-571 (P-1123-4, «Подивися в БД ecr»): рядки, які стара система РАХУВАЛА методологією M, але власні правила M їх не
    відбирають (C11_10: «34 комбінації» на 9_04/9_07) — звідки їх брала стара система, і які правила треба додати M у новій версії.
.DESCRIPTION
    Еталон — фактичні результати старої системи (dbo.CalcResult_* з колонками Methodology, LandTable, EFID; база ECR лише читається):
      1. для M і кожної таблиці AF, де є результати M, — рядки таблиці AF за EFID цих результатів;
      2. рядок, який НЕ відбирає жодне власне правило M (dbo.AF_EmissionCalculationWork_Rules, «параметр = значення», «*» — уся таблиця),
         шукається серед правил ІНШИХ методологій тієї ж таблиці: перше, що відбирає, — «позичене» правило;
      3. позичене правило в цільовій базі — правило чинної версії тієї іншої методології з тим самим набором значень (MatchJson), тож
         Id колонок і записів довідників уже правильні; його MatchJson додається M новим правилом.
    Рядок, для якого правила не знайдено ні серед власних, ні серед чужих, — у звіті як «без правила» (питання методологу).
    ✎ c11-10: 4. позичається лише правило, чий NAE M може віднести до категорії (Select-MsBorrowableRules, MsRowNarrowing.Lib.ps1);
    решта — у unmapped («NAE outside … categories»). Запис результату старої системи без значень ще не означає, що M рахує рядок.
    Значення (НАЕ, інвентарні номери) пишуться лише в -Out (тека фази поза репозиторієм), не в журнал.
.PARAMETER Methodology  Методології для аналізу (за замовчуванням ECW_C11_10).
#>
param(
    [string]$Server = 'localhost',
    [string]$ReferenceDatabase = 'ECR',
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$Out,
    [string[]]$Methodology = @('ECW_C11_10', 'ECW_C11_15')
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$Methodology = @($Methodology | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if (@('localhost', '.', '(local)', '127.0.0.1') -notcontains $Server) { throw 'База еталонів має бути локальною.' }
if ($ReferenceDatabase -match '^EcrLand' -or $ReferenceDatabase -eq $Database) { throw 'ReferenceDatabase має бути базою старої системи, не стендом ECR.' }
$refCs = "Server=$Server;Database=$ReferenceDatabase;Integrated Security=true;ApplicationIntent=ReadOnly;TrustServerCertificate=True"
$tgtCs = "Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=True"

function Read-Rows([string]$cs, [string]$sql, [hashtable]$params = @{}) {
    $cn = New-Object System.Data.SqlClient.SqlConnection($cs); $cn.Open()
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 600
        foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) }
        $rd = $cmd.ExecuteReader(); $list = New-Object System.Collections.Generic.List[hashtable]
        while ($rd.Read()) { $row = @{}; for ($i = 0; $i -lt $rd.FieldCount; $i++) { if (-not $rd.IsDBNull($i)) { $row[$rd.GetName($i)] = $rd.GetValue($i) } }; $list.Add($row) }
        $rd.Close(); return , $list
    } finally { $cn.Close() }
}

. (Join-Path $PSScriptRoot 'MsPhase.Lib.ps1')
. (Join-Path $PSScriptRoot 'MsRowNarrowing.Lib.ps1')

$ruleRows = Read-Rows $refCs "SELECT MInfo_Name AS M, RuleArg_TableLandFromDB AS T, ISNULL(RuleArg_Parameters, '') AS P, ISNULL(RuleArg_Values, '') AS V FROM dbo.AF_EmissionCalculationWork_Rules WITH (NOLOCK) WHERE RInfo_IsAvailable = 1"
$rules = @($ruleRows | ForEach-Object { ConvertTo-MsLegacyRule -Methodology ([string]$_['M']) -Table ([string]$_['T']) -Parameters ([string]$_['P']) -Values ([string]$_['V']) })
$resultTableRows = Read-Rows $refCs "SELECT t.name FROM sys.tables t WHERE t.name LIKE 'CalcResult[_]Emissions[_]Air[_]Land[_]%' AND t.name NOT LIKE '%History' AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = 'LandTable') AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = 'EFID') AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = 'Methodology')"
$resultTables = @($resultTableRows | ForEach-Object { [string]$_['name'] })

$result = New-Object System.Collections.Generic.List[object]
$summary = New-Object System.Collections.Generic.List[string]
foreach ($m in $Methodology) {
    $cur = Read-Rows $tgtCs "SELECT TOP 1 v.Id, v.MethodologyId FROM calc.MethodologyVersion v JOIN calc.Methodology m ON m.Id = v.MethodologyId WHERE m.Code = @m AND v.Status = 1 ORDER BY v.EffectiveFrom DESC, v.Id DESC" @{ '@m' = $m }
    if ($cur.Count -eq 0) { $summary.Add("${m}: немає опублікованої версії в цільовій базі"); continue }
    $own = @($rules | Where-Object { $_.Methodology -eq $m })
    $borrowed = [ordered]@{}; $unruled = @{}
    foreach ($rt in $resultTables) {
        $landTables = Read-Rows $refCs "SELECT DISTINCT LandTable FROM dbo.[$rt] WITH (NOLOCK) WHERE Methodology = @m" @{ '@m' = $m }
        foreach ($lt in $landTables) {
            $t = [string]$lt['LandTable']
            if ($t -notmatch '^AF_[A-Za-z0-9_]+$') { continue }
            $rows = Read-Rows $refCs "SELECT a.* FROM dbo.[$t] a WITH (NOLOCK) WHERE a.EFID IN (SELECT r.EFID FROM dbo.[$rt] r WITH (NOLOCK) WHERE r.Methodology = @m AND r.LandTable = @t)" @{ '@m' = $m; '@t' = $t }
            $ownT = @($own | Where-Object { $_.Table -eq $t }); $otherT = @($rules | Where-Object { $_.Table -eq $t -and $_.Methodology -ne $m })
            foreach ($row in $rows) {
                if (@($ownT | Where-Object { Test-MsLegacyRule -Rule $_ -Row $row }).Count -gt 0) { continue }
                $hit = @($otherT | Where-Object { Test-MsLegacyRule -Rule $_ -Row $row }) | Select-Object -First 1
                if ($null -eq $hit) { $unruled["$t"] = 1 + [int]$unruled["$t"]; continue }
                $key = $hit.Methodology + '|' + $t + '|' + (($hit.Conditions.Keys | ForEach-Object { "$_=$($hit.Conditions[$_])" }) -join ';')
                if (-not $borrowed.Contains($key)) { $borrowed[$key] = [pscustomobject]@{ Rule = $hit; Rows = 0 } }
                $borrowed[$key].Rows++
            }
        }
    }

    # Позичене правило -> правило чинної версії «іншої» методології в цільовій базі з тим самим набором значень.
    $mapped = New-Object System.Collections.Generic.List[object]; $unmapped = New-Object System.Collections.Generic.List[string]
    # ⚠ Read-Rows повертає список одним об'єктом: спершу змінна, потім індекс (інакше [0] — сам список, і максимум «0»).
    $maxRows = Read-Rows $tgtCs 'SELECT ISNULL(MAX(Priority), 0) AS P FROM calc.MethodologyRule WHERE MethodologyVersionId = @v' @{ '@v' = [int]$cur[0]['Id'] }
    $ownMax = [int]$maxRows[0]['P']
    $n = 0
    # Правила, які чинна версія ВЖЕ має (набір значень), — не додаються вдруге: повтор фази після нової версії нічого не множить.
    $liveRules = Read-Rows $tgtCs 'SELECT MatchJson FROM calc.MethodologyRule WHERE MethodologyVersionId = @v' @{ '@v' = [int]$cur[0]['Id'] }
    $liveSets = @($liveRules | ForEach-Object { (@(([string]$_['MatchJson'] | ConvertFrom-Json).PSObject.Properties | ForEach-Object { [string]$_.Value } | Sort-Object) -join [char]1) })


    # ✎ P-1123-6: ВЛАСНЕ правило старої системи, якого чинна версія не має (C11_15: у версії live 0 правил, у ECR — правило
    # 9a_21a «Land_NAE = …») — додається з колонкою таблиці ОСТАННЬОЇ опублікованої версії шаблону (Land_NAE у правилах — фізична
    # колонка IDCodeNAEProject, як у правилах інших методологій); значення — текст (колонка-довідник без Id запису — у звіт).
    foreach ($r in $own) {
        if ($r.All) { continue }
        $key = (@($r.Conditions.Values | ForEach-Object { [string]$_ } | Sort-Object) -join [char]1)
        if ($liveSets -contains $key) { continue }
        $tcode = $null
        if ($r.Table -match '^AF_Air_Land_ProcessUnits_9a_(\w+)$') { $tcode = 'T9a_' + $Matches[1] }
        elseif ($r.Table -match '^AF_Air_Land_Utility_9_(\d+)$') { $tcode = 'T9_' + $Matches[1] }
        elseif ($r.Table -match '^AF_Air_Land_Other_Equipment_5_(\d+)$') { $tcode = 'T5_' + $Matches[1] }
        if (-not $tcode) { $unmapped.Add("own rule $($r.Table): table not mapped"); continue }
        $json = [ordered]@{}; $ok = $true
        foreach ($k in $r.Conditions.Keys) {
            $colCode = if ($k -eq 'Land_NAE') { 'IDCodeNAEProject' } else { $k }
            $col = Read-Rows $tgtCs "SELECT TOP 1 c.Id, c.DataType FROM cfg.ColumnDef c JOIN cfg.TableDef td ON td.Id = c.TableDefId JOIN cfg.SheetDef s ON s.Id = td.SheetDefId JOIN cfg.TemplateVersion tv ON tv.Id = s.TemplateVersionId WHERE td.Code = @t AND c.Code = @c AND c.IsDeleted = 0 AND tv.Status = 1 ORDER BY tv.PublishedAt DESC, tv.Id DESC" @{ '@t' = $tcode; '@c' = $colCode }
            if ($col.Count -eq 0 -or [int]$col[0]['DataType'] -eq 5) { $ok = $false; break }
            $json[[string]$col[0]['Id']] = [string]$r.Conditions[$k]
        }
        if (-not $ok) { $unmapped.Add("own rule $($r.Table): column not mapped"); continue }
        $n++
        $mapped.Add([ordered]@{ code = ('R_{0}_E{1:00}' -f ($m -replace '^ECW_', ''), $n); matchJson = ($json | ConvertTo-Json -Compress); priority = $ownMax + 10 * $n; fromMethodology = $m; rows = 0 })
    }
    # ✎ c11-10: позичається лише правило, чиї рядки M може віднести до категорії. Інакше нова версія M відбирає рядки, які її ж
    # правило категорії відкидає (W15z: 28 правил ECW_C11_10 -> CategoryRuleRejected на live doc 96, жодного значення).
    $vid = [int]$cur[0]['Id']
    $engineRows = Read-Rows $tgtCs 'SELECT v.Engine, v.ScriptText, r.Expression FROM calc.MethodologyVersion v LEFT JOIN calc.CategoryRule r ON r.MethodologyVersionId = v.Id WHERE v.Id = @v' @{ '@v' = $vid }
    $catRows = Read-Rows $tgtCs 'SELECT Code FROM calc.MethodologyCategory WHERE MethodologyVersionId = @v AND IsRetired = 0' @{ '@v' = $vid }
    $facts = $null
    if ([int]$engineRows[0]['Engine'] -eq 1) {
        $text = [string]$engineRows[0]['ScriptText']
        if ($text -match '(?m)^var Category = ') { $facts = Get-MsScriptCategoryFacts -ScriptText $text }
    } else {
        $facts = Get-MsFormulaCategoryFacts -Expression ([string]$engineRows[0]['Expression']) -Categories @($catRows | ForEach-Object { [string]$_['Code'] })
    }
    $selection = Select-MsBorrowableRules -Rules @($borrowed.Values | ForEach-Object { $_.Rule }) -Facts $facts
    foreach ($x in $selection.Rejected) { $unmapped.Add("$($x.Methodology) $($x.Table): NAE outside $m categories (c11-10, rule not borrowed)") }
    foreach ($b in @($borrowed.Values | Where-Object { $selection.Kept -contains $_.Rule })) {
        $src = Read-Rows $tgtCs "SELECT r.MatchJson FROM calc.MethodologyRule r JOIN calc.MethodologyVersion v ON v.Id = r.MethodologyVersionId JOIN calc.Methodology mm ON mm.Id = v.MethodologyId WHERE mm.Code = @x AND v.Status = 1 AND v.EffectiveFrom = (SELECT MAX(x.EffectiveFrom) FROM calc.MethodologyVersion x WHERE x.MethodologyId = v.MethodologyId AND x.Status = 1)" @{ '@x' = $b.Rule.Methodology }
        $want = @($b.Rule.Conditions.Values | ForEach-Object { [string]$_ } | Sort-Object)
        if ($liveSets -contains (@($want) -join [char]1)) { continue }
        $match = $null
        foreach ($s in $src) {
            $vals = @(([string]$s['MatchJson'] | ConvertFrom-Json).PSObject.Properties | ForEach-Object { [string]$_.Value } | Sort-Object)
            if ((@($vals) -join [char]1) -eq (@($want) -join [char]1)) { $match = [string]$s['MatchJson']; break }
        }
        if ($null -eq $match) { $unmapped.Add("$($b.Rule.Methodology) $($b.Rule.Table) ($($b.Rows) rows)"); continue }
        $n++
        $mapped.Add([ordered]@{ code = ('R_{0}_B{1:00}' -f ($m -replace '^ECW_', ''), $n); matchJson = $match; priority = $ownMax + 10 * $n; fromMethodology = $b.Rule.Methodology; rows = $b.Rows })
    }
    $result.Add([ordered]@{ methodologyCode = $m; versionId = [int]$cur[0]['Id']; rules = @($mapped.ToArray()); unmapped = @($unmapped.ToArray()); unruled = $unruled })
    $borrowedRows = 0; foreach ($x in $mapped) { $borrowedRows += [int]$x['rows'] }
    $unruledRows = 0; foreach ($x in $unruled.Values) { $unruledRows += [int]$x }
    $summary.Add(("{0}: позичених правил {1} (рядків {2}), без відповідника в цільовій базі {3}, рядків без жодного правила {4}" -f $m, $mapped.Count, $borrowedRows, $unmapped.Count, $unruledRows))
}
$dir = Split-Path -Parent ([IO.Path]::GetFullPath($Out)); if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Out), (@{ format = 'ecr-ms-borrowed-rules'; formatVersion = 1; referenceDatabase = $ReferenceDatabase; versions = @($result.ToArray()) } | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
$summary | ForEach-Object { Write-Host $_ }
