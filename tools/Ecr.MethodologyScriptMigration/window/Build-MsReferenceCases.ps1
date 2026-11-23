<#
.SYNOPSIS
    ✎ D-571 (P-1123-4): входи паритету «формули ↔ скрипт» з ЕТАЛОННИХ даних старої системи (база ECR, лише читання) — для версій,
    яких дані live не доводять. Результат — cases.json для `Ecr.MethodologyScriptMigration parity-cases`.
.DESCRIPTION
    Для кожної чинної опублікованої версії рушія Formulas рівня Configuration у цільовій базі (копія live):
      1. правила старої системи (dbo.AF_EmissionCalculationWork_Rules, RInfo_IsAvailable = 1) дають таблиці AF і умови «параметр = значення»;
      2. таблиця AF -> таблиця шаблону Land (Get-LandLegacyTableMap, модель шаблону);
      3. до -MaxRowsPerVersion найсвіжіших рядків за умовами (Land_Status = Running, якщо колонка є), рівномірно за правилами;
      4. аргументи версії (ArgumentsCsv формул + @-аргументи правила категорії) — з колонок рядка тим самим правилом, що й золотий знімок
         LegacyPublish (Get-LandGoldenRowColumn: сам аргумент, псевдонім, суфікс K1, колонка-посилання [X]); Lookup-колонка — Id запису
         довідника цільової бази (Find-LandLegacyEntryCode за моделлю -> dic.RegistryEntry).
    ⛔ База еталонів — лише читання (ApplicationIntent=ReadOnly, лише SELECT ... WITH (NOLOCK)), лише локальна, не стенд EcrLand*.
    Значення в журнал не пишуться; cases.json лишається поза репозиторієм (тека фази).
.PARAMETER ReferenceDatabase  База старої системи з еталонними даними (ECR).
.PARAMETER Database           Цільова база (копія live), звідки версії, прив'язки й Id записів довідників.
.PARAMETER Out                Файл cases.json.
#>
param(
    [string]$Server = 'localhost',
    [string]$ReferenceDatabase = 'ECR',
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$Out,
    [int]$MaxRowsPerVersion = 25,
    [long]$DocumentId = 0,
    [int]$PeriodKey = 0,
    [string[]]$OnlyMethodology = @(),
    [string]$ModelPath
)
# Бібліотеки Land (LandLegacy*.Lib.ps1) читають необов'язкові властивості моделі без перевірки — як і в Build-LandTemplate.ps1.
Set-StrictMode -Off
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
. (Join-Path $repo 'tools\land\LandLegacyRules.Lib.ps1')
. (Join-Path $repo 'tools\land\LandLegacyPublish.Lib.ps1')
$OnlyMethodology = @($OnlyMethodology | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })

if (@('localhost', '.', '(local)', '127.0.0.1') -notcontains $Server) { throw 'База еталонів має бути локальною.' }
if ($ReferenceDatabase -match '^EcrLand') { throw 'ReferenceDatabase вказує на стенд ECR, а не на базу старої системи.' }
if ($Database -eq $ReferenceDatabase) { throw 'Цільова база і база еталонів однакові.' }
$refCs = "Server=$Server;Database=$ReferenceDatabase;Integrated Security=true;ApplicationIntent=ReadOnly;TrustServerCertificate=True"
$tgtCs = "Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=True"

function Read-Rows([string]$cs, [string]$sql, [hashtable]$params = @{}) {
    $cn = New-Object System.Data.SqlClient.SqlConnection($cs); $cn.Open()
    try {
        $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 300
        foreach ($k in $params.Keys) { [void]$cmd.Parameters.AddWithValue($k, $params[$k]) }
        $rd = $cmd.ExecuteReader(); $list = New-Object System.Collections.Generic.List[hashtable]
        while ($rd.Read()) {
            $row = @{}
            for ($i = 0; $i -lt $rd.FieldCount; $i++) { if (-not $rd.IsDBNull($i)) { $row[$rd.GetName($i)] = $rd.GetValue($i) } }
            $list.Add($row)
        }
        $rd.Close(); return , $list
    } finally { $cn.Close() }
}

if (-not $ModelPath) { $ModelPath = Join-Path $repo 'docs\delivery\reference-data\land-template\land-template.model.json' }
$model = Get-Content -LiteralPath $ModelPath -Raw -Encoding UTF8 | ConvertFrom-Json
$tables = @{}
foreach ($sh in @($model.sheets)) { foreach ($tb in @($sh.tables)) { if (-not $tables.ContainsKey([string]$tb.code)) { $tables[[string]$tb.code] = $tb } } }
$regs = @{}
foreach ($r in @($model.registries)) { $regs[[string]$r.code] = $r }

# Документ і період контексту прогону: за замовчуванням — найраніший оригінальний екземпляр будь-якої таблиці Land у цільовій базі.
if ($DocumentId -le 0 -or $PeriodKey -le 0) {
    $ctx = Read-Rows $tgtCs 'SELECT TOP 1 i.DocumentId, i.PeriodKey FROM doc.TableInstance i JOIN cfg.CalculationBinding b ON b.TableDefId = i.TableDefId AND b.IsActive = 1 WHERE i.SheetCopyId IS NULL ORDER BY i.PeriodKey, i.DocumentId'
    $DocumentId = [long]$ctx[0]['DocumentId']; $PeriodKey = [int]$ctx[0]['PeriodKey']
}

$versions = Read-Rows $tgtCs @'
SELECT v.Id, m.Id AS MethodologyId, m.Code, v.Version
FROM calc.MethodologyVersion v JOIN calc.Methodology m ON m.Id = v.MethodologyId
WHERE v.Status = 1 AND v.Engine = 0 AND v.Level = 1 AND m.Kind <> 2
  AND v.EffectiveFrom = (SELECT MAX(x.EffectiveFrom) FROM calc.MethodologyVersion x WHERE x.MethodologyId = v.MethodologyId AND x.Status = 1)
ORDER BY m.Code
'@
$rules = Read-Rows $refCs "SELECT MInfo_Name, RuleArg_TableLandFromDB, ISNULL(RuleArg_Parameters, '') AS P, ISNULL(RuleArg_Values, '') AS V FROM dbo.AF_EmissionCalculationWork_Rules WITH (NOLOCK) WHERE RInfo_IsAvailable = 1"
$tmap = Get-LandLegacyTableMap -Model $model -LegacyTables @($rules | ForEach-Object { [string]$_['RuleArg_TableLandFromDB'] } | Sort-Object -Unique)
$entryIds = @{}
foreach ($e in (Read-Rows $tgtCs 'SELECT d.Code AS R, e.Code AS E, e.Id FROM dic.RegistryEntry e JOIN cfg.RegistryDef d ON d.Id = e.RegistryDefId WHERE e.IsDeleted = 0')) {
    $entryIds[([string]$e['R'] + '|' + [string]$e['E'])] = [long]$e['Id']
}
$refColumns = @{}
function Get-RefColumns([string]$t) {
    if (-not $refColumns.ContainsKey($t)) {
        $h = @{}; foreach ($c in (Read-Rows $refCs 'SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t)' @{ '@t' = "dbo.$t" })) { $h[[string]$c['name']] = $true }
        $refColumns[$t] = $h
    }
    return $refColumns[$t]
}

$result = New-Object System.Collections.Generic.List[object]
$summary = New-Object System.Collections.Generic.List[string]

# Похідні колонки шаблону, яких рядок старої системи не має (формули шаблону над періодом, місячними колонками й складом газу
# HSE400). Обчислюються з часу рядка (Time) і довідників ЦІЛЬОВОЇ бази тим самим виразом, що в шаблоні; інакше — $null.
$streamRows = @{}
foreach ($s in (Read-Rows $tgtCs "SELECT e.Id, f.Code, v.ValueString FROM dic.RegistryEntry e JOIN cfg.RegistryDef d ON d.Id = e.RegistryDefId AND d.Code = 'LAND_HSE400_STREAM' JOIN dic.RegistryValue v ON v.RegistryEntryId = e.Id JOIN cfg.RegistryFieldDef f ON f.Id = v.RegistryFieldDefId WHERE e.IsDeleted = 0")) {
    $k = [string]$s['Id']; if (-not $streamRows.ContainsKey($k)) { $streamRows[$k] = @{} }; $streamRows[$k][[string]$s['Code']] = [string]$s['ValueString']
}
$prefixOfStream = @{}
foreach ($s in $streamRows.Values) { if ($s['TEXT'] -and $s['PREFIX']) { $prefixOfStream[(ConvertTo-LandLegacyNorm $s['TEXT'])] = $s['PREFIX'] } }
$composition = @{}
foreach ($c in (Read-Rows $tgtCs "SELECT e.Code, v.ValueNumeric FROM dic.RegistryEntry e JOIN cfg.RegistryDef d ON d.Id = e.RegistryDefId AND d.Code = 'LAND_HSE400_COMPOSITION' JOIN dic.RegistryValue v ON v.RegistryEntryId = e.Id JOIN cfg.RegistryFieldDef f ON f.Id = v.RegistryFieldDefId AND f.Code = 'VALUE' WHERE e.IsDeleted = 0 AND v.ValueNumeric IS NOT NULL")) {
    $composition[[string]$c['Code']] = [decimal]$c['ValueNumeric']
}
function Resolve-TemplateDerived([string]$code, $tb, [hashtable]$row) {
    $col = @($tb.columns | Where-Object { [string]$_.code -eq $code }) | Select-Object -First 1
    if ($null -eq $col -or -not $row.ContainsKey('Time')) { return $null }
    $expr = [string]$col.expression; $time = [datetime]$row['Time']
    if ($expr -match '^\s*\[Period\]\.Sequence\s*$') { return [decimal]$time.Month }
    if ($expr -match '^\s*\(\[Period\]\.Year % 4 = 0\) \? 366 : 365\s*$') { return [decimal]$(if ($time.Year % 4 -eq 0) { 366 } else { 365 }) }
    if ($expr -match '^\s*\(\[Period\]\.Sequence < 3 OR \[Period\]\.Sequence > 8\) \? 1 : 0\s*$') { return [decimal]$(if ($time.Month -lt 3 -or $time.Month -gt 8) { 1 } else { 0 }) }
    # вибір місячної колонки [X_Mnn] за [Period].Sequence: у рядку старої системи — сама величина X за місяць рядка.
    $mm = [regex]::Match($expr, '^\s*\[Period\]\.Sequence = 1 \? \[(?<b>[A-Za-z0-9_]+)_M01\]')
    if ($mm.Success -and $row.ContainsKey($mm.Groups['b'].Value) -and $row[$mm.Groups['b'].Value] -isnot [string]) { return [decimal]$row[$mm.Groups['b'].Value] }
    # склад газу HSE400: REGFIND('LAND_HSE400_COMPOSITION', рік, місяць, PREFIX потоку, '<показник>') -> VALUE
    $ci = [regex]::Match($expr, "LAND_HSE400_COMPOSITION'.*?'PREFIX'\),\s*'(?<ind>[A-Za-z0-9_]+)'\),\s*'VALUE'\)")
    if (-not $ci.Success) { return $null }
    $stream = $null
    if ($expr -match "REGFIELD\(\[Land_TypeFuel\], 'NAME'\)") {
        if (-not $row.ContainsKey('Land_TypeFuel')) { return $null }
        $stream = [string]$row['Land_TypeFuel']
        foreach ($ex in [regex]::Matches($expr, "REGFIELD\(\[Land_TypeFuel\], 'NAME'\) = '(?<n>[^']+)'")) { if ($ex.Groups['n'].Value -eq $stream) { return $null } }
    } elseif ($expr -match '\[TypeOfStreams\]') {
        $src = Get-LandGoldenRowColumn -Argument 'TypeOfStreams' -Row $row -Columns @($tb.columns)
        if ($null -eq $src) { return $null }
        $stream = [string]$row[$src]
    } else { return $null }
    $prefix = $prefixOfStream[(ConvertTo-LandLegacyNorm $stream)]
    if (-not $prefix) { return $null }
    $key = 'H{0:0000}{1:00}_{2}_{3}' -f $time.Year, $time.Month, $prefix, $ci.Groups['ind'].Value
    if ($composition.ContainsKey($key)) { return $composition[$key] }
    return $null
}

foreach ($v in $versions) {
    $code = [string]$v['Code']; $vid = [int]$v['Id']
    if ($OnlyMethodology.Count -gt 0 -and $OnlyMethodology -notcontains $code) { continue }
    $args1 = New-Object System.Collections.Generic.HashSet[string]
    foreach ($f in (Read-Rows $tgtCs 'SELECT ArgumentsCsv FROM calc.MethodologyFormula WHERE MethodologyVersionId = @v' @{ '@v' = $vid })) {
        # ArgumentsCsv: «@Arg[?]» — аргументи рядка; «CST.X» — константи, «!F» — формули: їх рядок не дає.
        foreach ($a in ([string]$f['ArgumentsCsv'] -split '[;,]')) { $n = $a.Trim(); if ($n.StartsWith('@')) { $n = $n.Substring(1).TrimEnd('?'); if ($n) { [void]$args1.Add($n) } } }
    }
    foreach ($cr in (Read-Rows $tgtCs 'SELECT Expression FROM calc.CategoryRule WHERE MethodologyVersionId = @v' @{ '@v' = $vid })) {
        foreach ($mm in [regex]::Matches([string]$cr['Expression'], '@([A-Za-z_][A-Za-z0-9_]*)')) { [void]$args1.Add($mm.Groups[1].Value) }
    }
    $bound = @{}; foreach ($b in (Read-Rows $tgtCs 'SELECT DISTINCT t.Code FROM cfg.CalculationBinding b JOIN cfg.TableDef t ON t.Id = b.TableDefId WHERE b.MethodologyId = @m AND b.IsActive = 1' @{ '@m' = [int]$v['MethodologyId'] })) { $bound[[string]$b['Code']] = $true }
    $myRules = @($rules | Where-Object { [string]$_['MInfo_Name'] -eq $code })
    if ($myRules.Count -eq 0) { $summary.Add("$code ${vid}: немає правил у базі еталонів"); continue }
    $perRule = [Math]::Max(1, [int][Math]::Ceiling($MaxRowsPerVersion / $myRules.Count))
    $cases = New-Object System.Collections.Generic.List[object]
    $n = 0
    foreach ($rule in $myRules) {
        if ($cases.Count -ge $MaxRowsPerVersion) { break }
        $lt = [string]$rule['RuleArg_TableLandFromDB']; $tcode = $tmap[$lt]
        if (-not $tcode -or -not $bound.ContainsKey($tcode)) { continue }
        $have = Get-RefColumns $lt
        if ($have.Count -eq 0) { continue }
        $ps = @(([string]$rule['P']).TrimEnd(';') -split ';' | Where-Object { $_ }); $vs = @(([string]$rule['V']).TrimEnd(';') -split ';')
        $where = @('1 = 1'); $prm = @{}
        for ($i = 0; $i -lt $ps.Count; $i++) {
            if ($ps[$i] -eq '*') { continue }
            if (-not $have.ContainsKey($ps[$i])) { $where = $null; break }
            $where += "[$($ps[$i])] = @p$i"; $prm["@p$i"] = [string]$vs[$i]
        }
        if ($null -eq $where) { continue }
        if ($have.ContainsKey('Land_Status')) { $where += "Land_Status LIKE 'Running%'" }
        $order = if ($have.ContainsKey('Time')) { ' ORDER BY Time DESC' } else { '' }
        $rows = Read-Rows $refCs ("SELECT TOP $perRule * FROM dbo.[$lt] WITH (NOLOCK) WHERE " + ($where -join ' AND ') + $order) $prm
        $tb = $tables[$tcode]
        foreach ($row in $rows) {
            $argList = New-Object System.Collections.Generic.List[object]
            foreach ($a in $args1) {
                $src = Get-LandGoldenRowColumn -Argument $a -Row $row -Columns @($tb.columns)
                if ($null -eq $src) {
                    $derived = Resolve-TemplateDerived $a $tb $row
                    if ($null -ne $derived) { $argList.Add([ordered]@{ argumentCode = $a; value = $derived; valueString = $null; entryId = $null }) }
                    continue
                }
                $val = $row[$src]
                $colDef = @($tb.columns | Where-Object { [string]$_.code -eq $src }) | Select-Object -First 1
                $lookup = Get-LandGoldenLookupRegistry -Column $colDef -Columns @($tb.columns)
                if ($lookup -and $regs.ContainsKey($lookup)) {
                    $ec = Find-LandLegacyEntryCode -Registry $regs[$lookup] -Value ([string]$val)
                    if ($ec -and $ec -ne '#ambiguous' -and $entryIds.ContainsKey("$lookup|$ec")) {
                        $argList.Add([ordered]@{ argumentCode = $a; value = $null; valueString = $null; entryId = $entryIds["$lookup|$ec"] }); continue
                    }
                }
                if ($val -is [string] -or $val -is [bool] -or $val -is [guid]) { $argList.Add([ordered]@{ argumentCode = $a; value = $null; valueString = [string]$val; entryId = $null }) }
                elseif ($val -is [datetime]) { $argList.Add([ordered]@{ argumentCode = $a; value = $null; valueString = $val.ToString('s'); entryId = $null }) }
                else { $argList.Add([ordered]@{ argumentCode = $a; value = [decimal]$val; valueString = $null; entryId = $null }) }
            }
            $n++
            $cases.Add([ordered]@{ key = ('REF{0:000}' -f $n); table = $tcode; arguments = @($argList.ToArray()) })
            if ($cases.Count -ge $MaxRowsPerVersion) { break }
        }
    }
    $summary.Add("$code ${vid}: рядків-еталонів $($cases.Count)")
    if ($cases.Count -gt 0) {
        $result.Add([ordered]@{ versionId = $vid; methodologyCode = $code; version = [string]$v['Version']; cases = @($cases.ToArray()) })
    }
}

$doc = [ordered]@{ format = 'ecr-ms-reference-cases'; formatVersion = 1; referenceDatabase = $ReferenceDatabase; documentId = $DocumentId; periodKey = $PeriodKey; versions = @($result.ToArray()) }
$dir = Split-Path -Parent ([IO.Path]::GetFullPath($Out)); if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Out), ($doc | ConvertTo-Json -Depth 10), (New-Object Text.UTF8Encoding($false)))
$summary | ForEach-Object { Write-Host $_ }
Write-Host "cases: версій $($result.Count) -> $Out"
