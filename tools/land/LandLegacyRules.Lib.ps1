# tools/land/LandLegacyRules.Lib.ps1
# Правила рядків методологій зі старої системи (AF_EmissionCalculationWork_Rules) -> calc.MethodologyRule.MatchJson.
# Чиста логіка без звернень до API: колонки й записи довідників підставляє викликач (скриптблоки), значень прода тут немає.
#
# Вхід - пакет `ecr-methodology-rules` v1 (tools/Ecr.MethodologyImport: `export --sql ... --rules-out`): на правило - таблиця
# старої системи (RuleArg_TableLandFromDB), умови «параметр = значення» (RuleArg_Parameters / RuleArg_Values, текст як у старій
# системі) або «уся таблиця» (`*`).
# Вихід - плаский предикат `{"<ColumnDefId>":"<значення>"}`: для колонки-довідника значення - Id запису, інакше - текст.
#
#   Get-LandLegacyTableMap        - таблиця старої системи -> код таблиці шаблону (мапінг моделі + іменування AF);
#   Get-LandLegacyColumnCode      - параметр правила -> код колонки таблиці шаблону (точний збіг, `<імя>Ref`, псевдонім, префікс);
#   Find-LandLegacyEntryCode      - текст значення -> код запису довідника колонки (name, en, ru; однозначно);
#   Convert-LandLegacyRule        - одне правило -> предикат або причина відмови;
#   Get-LandLegacyRulePlan        - правила версії -> план PUT (коди, пріоритети; «уся таблиця» - один `{}` з найбільшим пріоритетом).


# Параметри старої системи, чиє ім'я в шаблоні не збігається ні точно, ні з суфіксом Ref, ні як префікс.
$script:LandLegacyColumnAliases = @{
    'Land_DescEqp' = 'Land_DescriptionEquipment'
}
# Параметри, які в шаблоні мають лише фізичну колонку (моста Formula немає): ім'я колонки в шаблоні - як у завантажувача
# (LandRows.Lib.ps1: Land_InvNumEqp -> InventoryNumberOfEquipment).
$script:LandLegacyPhysicalColumns = @{
    'Land_InvNumEqp' = 'InventoryNumberOfEquipment'
}

function ConvertTo-LandLegacyNorm([string]$Text) {
    if ($null -eq $Text) { return '' }
    return (($Text -replace '\s+', ' ').Trim()).ToLowerInvariant()
}

function Get-LandLegacyTableMap {
    <#
    .SYNOPSIS
        Словник «таблиця старої системи -> код таблиці шаблону» для всіх таблиць моделі, які мають відповідник.
    .DESCRIPTION
        Спершу явний prodColumnMapping.prodTables моделі (`ШИФР_АРКУША|ТАБЛИЦЯ`), далі - іменування AF:
        Stationary_Equipment -> T3_00, Mobile_Equipment -> T4_01, Other_Equipment_5_NN -> T5_NN, Dust_..._6_NN -> T6_NN,
        Utility_9_NN -> T9_NN, ProcessUnits_9a_XX -> T9a_XX, Off_Utility_DI_10_NN -> T10_NN, Off_Utility_AI_10a_NN ->
        T10a_NN, Off_Process_DI_11_NN -> T11_NN, 11x_Off_Process_* -> T11x_01, Water_Land_07a_N -> T7a_NN, 07b -> T7b_00,
        07 -> T7_00, Sulfur -> T9b_00. Код приймається лише якщо така таблиця є в моделі (Codes).
    #>
    param([Parameter(Mandatory)]$Model, [Parameter(Mandatory)][string[]]$LegacyTables)

    $codes = @{}
    foreach ($sh in @($Model.sheets)) { foreach ($tb in @($sh.tables)) { $codes[[string]$tb.code] = $true } }
    $explicit = @{}
    if ($Model.prodColumnMapping -and $Model.prodColumnMapping.prodTables) {
        foreach ($p in $Model.prodColumnMapping.prodTables.PSObject.Properties) { $explicit[[string]$p.Name] = ([string]$p.Value -split '\|')[-1] }
    }
    $map = [ordered]@{}
    foreach ($lt in ($LegacyTables | Sort-Object -Unique)) {
        $code = $null
        if ($explicit.ContainsKey($lt)) { $code = $explicit[$lt] }
        elseif ($lt -eq 'AF_Air_Land_Stationary_Equipment') { $code = 'T3_00' }
        elseif ($lt -eq 'AF_Air_Land_Mobile_Equipment') { $code = 'T4_01' }
        elseif ($lt -eq 'AF_Air_Land_Sulfur') { $code = 'T9b_00' }
        elseif ($lt -eq 'AF_Water_Land_07') { $code = 'T7_00' }
        elseif ($lt -eq 'AF_Water_Land_07b') { $code = 'T7b_00' }
        elseif ($lt -match '^AF_Air_Land_Other_Equipment_5_(\d+)$') { $code = 'T5_' + $Matches[1] }
        elseif ($lt -match '^AF_Air_Land_Dust_Producing_Activities_6_(\d+)$') { $code = 'T6_' + $Matches[1] }
        elseif ($lt -match '^AF_Air_Land_Utility_9_(\d+)$') { $code = 'T9_' + $Matches[1] }
        elseif ($lt -match '^AF_Air_Land_ProcessUnits_9a_(\w+)$') { $code = 'T9a_' + $Matches[1] }
        elseif ($lt -match '^AF_Air_Land_Off_Utility_DI_10_(\d+)$') { $code = 'T10_' + $Matches[1] }
        elseif ($lt -match '^AF_Air_Land_Off_Utility_AI_10a_(\d+)$') { $code = 'T10a_' + $Matches[1] }
        elseif ($lt -match '^AF_Air_Land_Off_Process_DI_11_(\d+)$') { $code = 'T11_' + $Matches[1] }
        elseif ($lt -match '^AF_Air_Land_11([a-d])_Off_Process_') { $code = 'T11' + $Matches[1] + '_01' }
        elseif ($lt -match '^AF_Water_Land_07a_(\d+)$') { $code = 'T7a_' + ('{0:00}' -f [int]$Matches[1]) }
        if ($code -and $codes.ContainsKey($code)) { $map[$lt] = $code } else { $map[$lt] = $null }
    }
    return $map
}

function Get-LandLegacyColumnCode {
    <#
    .SYNOPSIS
        Код колонки таблиці шаблону для параметра правила; $null, якщо не визначається однозначно.
    .PARAMETER Columns   Колонки таблиці моделі (code, dataType, registry).
    #>
    param([Parameter(Mandatory)][string]$Parameter, [Parameter(Mandatory)]$Columns, $Aliases = @())

    $by = @{}
    foreach ($c in @($Columns)) { $by[[string]$c.code] = $c }
    $ref = $Parameter + 'Ref'
    # Колонка-довідник `<імя>Ref` важливіша за однойменну Formula-колонку (T5_27: Land_TypeBurnFuel - вираз, Land_TypeBurnFuelRef - Lookup).
    if ($by.ContainsKey($ref) -and [string]$by[$ref].dataType -eq 'Lookup') { return $ref }
    if ($by.ContainsKey($Parameter)) { return $Parameter }
    if ($by.ContainsKey($ref)) { return $ref }
    if ($script:LandLegacyColumnAliases.ContainsKey($Parameter) -and $by.ContainsKey($script:LandLegacyColumnAliases[$Parameter])) { return $script:LandLegacyColumnAliases[$Parameter] }
    $pre = @($by.Keys | Where-Object { $_ -like "$Parameter*" -and $_ -notmatch '_M\d\d$' })
    if ($pre.Count -eq 1) { return [string]$pre[0] }
    # Мапінг моделі «атрибут прода -> колонка таблиці» (prodColumnMapping.tables[].aliases): Land_NAE -> IDCodeMPEProject у T6_12.
    foreach ($a in @($Aliases)) { if ([string]$a.prod -eq $Parameter -and $a.column -and $by.ContainsKey([string]$a.column)) { return [string]$a.column } }
    if ($script:LandLegacyPhysicalColumns.ContainsKey($Parameter) -and $by.ContainsKey($script:LandLegacyPhysicalColumns[$Parameter])) { return $script:LandLegacyPhysicalColumns[$Parameter] }
    return $null
}

function Find-LandLegacyEntryCode {
    <#
    .SYNOPSIS
        Код запису довідника за текстом значення правила; $null - немає, '#ambiguous' - більше одного.
    .DESCRIPTION
        Порядок: name дослівно; name без урахування регістру/пробілів; en; ru; name, що починається з тексту.
        Кожен крок має дати рівно один запис, інакше далі (на ambiguity зупинка).
    #>
    param([Parameter(Mandatory)]$Registry, [Parameter(Mandatory)][string]$Value)

    $entries = @($Registry.entries)
    $steps = @(
        { param($e) [string]$e.name -ceq $Value },
        { param($e) (ConvertTo-LandLegacyNorm $e.name) -eq (ConvertTo-LandLegacyNorm $Value) },
        { param($e) (ConvertTo-LandLegacyNorm $e.en) -eq (ConvertTo-LandLegacyNorm $Value) },
        { param($e) (ConvertTo-LandLegacyNorm $e.ru) -eq (ConvertTo-LandLegacyNorm $Value) },
        { param($e) (ConvertTo-LandLegacyNorm $e.name).StartsWith((ConvertTo-LandLegacyNorm $Value)) -and (ConvertTo-LandLegacyNorm $Value).Length -gt 0 }
    )
    foreach ($s in $steps) {
        $hit = @($entries | Where-Object { & $s $_ })
        if ($hit.Count -eq 1) { return [string]$hit[0].code }
        if ($hit.Count -gt 1) {
            # Однакові коди (двійник у моделі) - не двозначність.
            $codes = @($hit | ForEach-Object { [string]$_.code } | Sort-Object -Unique)
            if ($codes.Count -eq 1) { return $codes[0] }
            return '#ambiguous'
        }
    }
    return $null
}

function Convert-LandLegacyRule {
    <#
    .SYNOPSIS
        Одне правило пакета -> предикат MatchJson (як рядок) або причини, чому не вдалося.
    .PARAMETER Rule        Правило пакета (table, conditions[], catchAll).
    .PARAMETER TableMap    Результат Get-LandLegacyTableMap.
    .PARAMETER Tables      Словник «код таблиці -> таблиця моделі» (columns).
    .PARAMETER Registries  Словник «код довідника -> довідник моделі» (entries).
    .PARAMETER AliasesByTable  Словник «код таблиці -> prodColumnMapping.aliases» (необов'язково).
    .PARAMETER ColumnId    { param($tableCode, $columnCode) -> Id колонки версії шаблону або $null }.
    .PARAMETER EntryId     { param($registryCode, $entryCode) -> Id запису довідника або $null }.
    .PARAMETER StatusEntryId   Id запису `Running`, якщо до предиката додається `Land_Status = Running` (лише коли в таблиці є такий стовпець).
    #>
    param(
        [Parameter(Mandatory)]$Rule, [Parameter(Mandatory)]$TableMap, [Parameter(Mandatory)]$Tables, [Parameter(Mandatory)]$Registries,
        [Parameter(Mandatory)][scriptblock]$ColumnId, [Parameter(Mandatory)][scriptblock]$EntryId, $StatusEntryId = $null, $AliasesByTable = @{}
    )

    $reasons = New-Object System.Collections.Generic.List[string]
    $aliasMap = if ($null -eq $AliasesByTable) { @{} } else { $AliasesByTable }
    $tcode = $TableMap[[string]$Rule.table]
    if (-not $tcode) { return [pscustomobject]@{ Ok = $false; MatchJson = $null; TableCode = $null; Reasons = @("table:$($Rule.table)"); CatchAll = [bool]$Rule.catchAll } }
    $tb = $Tables[$tcode]
    $pred = [ordered]@{}
    foreach ($cond in @($Rule.conditions)) {
        $colCode = Get-LandLegacyColumnCode -Parameter ([string]$cond.parameter) -Columns $tb.columns -Aliases $aliasMap[$tcode]
        if (-not $colCode) { [void]$reasons.Add("column:$tcode/$($cond.parameter)"); continue }
        $col = @($tb.columns | Where-Object { [string]$_.code -eq $colCode }) | Select-Object -First 1
        $cid = & $ColumnId $tcode $colCode
        if (-not $cid) { [void]$reasons.Add("columnId:$tcode/$colCode"); continue }
        $condText = [string]$cond.value
        if ([string]$col.dataType -eq 'Lookup' -and $col.registry) {
            $reg = $Registries[[string]$col.registry]
            $ec = if ($reg) { Find-LandLegacyEntryCode -Registry $reg -Value $condText } else { $null }
            if (-not $ec) { [void]$reasons.Add("entry:$($col.registry)/notFound"); continue }
            if ($ec -eq '#ambiguous') { [void]$reasons.Add("entry:$($col.registry)/ambiguous"); continue }
            $eid = & $EntryId ([string]$col.registry) $ec
            if (-not $eid) { [void]$reasons.Add("entryId:$($col.registry)/$ec"); continue }
            $condText = [string]$eid
        }
        $pred[[string][int]$cid] = $condText
    }
    if ($reasons.Count -gt 0) { return [pscustomobject]@{ Ok = $false; MatchJson = $null; TableCode = $tcode; Reasons = $reasons.ToArray(); CatchAll = [bool]$Rule.catchAll } }
    $wasEmpty = ($pred.Count -eq 0)
    if ($null -ne $StatusEntryId -and @($tb.columns | Where-Object { [string]$_.code -eq 'Land_Status' }).Count -gt 0) {
        $sid = & $ColumnId $tcode 'Land_Status'
        if ($sid) { $pred[[string][int]$sid] = [string]$StatusEntryId }
    }
    # «Уся таблиця» (D-556, коло 4): правило нової системи НЕ прив'язане до таблиці, тож `{}` відбирав би рядки ВСІХ прив'язаних
    # таблиць методології, а в старій системі правило має таблицю. Через наявні можливості MatchJson таблицю задає колонка
    # статусу ЦІЄЇ таблиці (Id колонки належить одній таблиці): `{<Id статусу таблиці>: Running}`. Це рівнозначно «усій
    # таблиці» лише для методологій з фільтром активності Running (land-row-filter.json: -StatusEntryId задано): рядки
    # інших статусів фільтр відсікає однаково. Без колонки статусу або без -StatusEntryId лишається `{}` (CatchAll).
    $scopedCatchAll = ($wasEmpty -and $pred.Count -gt 0)   # `Land_Status` уже додано блоком вище
    if ($pred.Count -eq 0 -and $null -ne $StatusEntryId) {
        $statusCode = $null
        foreach ($cand in 'Land_Status', 'Status') {
            $sc = @($tb.columns | Where-Object { [string]$_.code -eq $cand -and [string]$_.dataType -eq 'Lookup' }) | Select-Object -First 1
            if ($sc) { $statusCode = $cand; break }
        }
        if ($statusCode) {
            $sid2 = & $ColumnId $tcode $statusCode
            if ($sid2) { $pred[[string][int]$sid2] = [string]$StatusEntryId; $scopedCatchAll = $true }
        }
    }
    $json = if ($pred.Count -eq 0) { '{}' } else { ($pred | ConvertTo-Json -Compress) }
    return [pscustomobject]@{ Ok = $true; MatchJson = $json; TableCode = $tcode; Reasons = @(); CatchAll = ($pred.Count -eq 0); TableScopedCatchAll = $scopedCatchAll }
}

function Get-LandLegacyRulePlan {
    <#
    .SYNOPSIS
        План запису правил версії: код, предикат, пріоритет (умовні - 10 x позиція правила в пакеті; `{}` - один, останній).
    .DESCRIPTION
        Правила «уся таблиця» різних таблиць зливаються в одне `{}`: правило нової системи не прив'язане до таблиці, а два
        правила з однаковим предикатом дали б ECR-CALC-0422 (рівний пріоритет/затінення). Недоступні (isAvailable=false)
        правила в план не входять. Те саме повторне предикат одного кроку (дублікат) лишається окремим правилом, якщо код інший.
    .OUTPUTS
        [pscustomobject] Rules (Code, MatchJson, Priority, Table, Source), Unresolved (Code, Table, Reasons), Skipped, Merged.
    #>
    param([Parameter(Mandatory)]$Converted)

    $plan = New-Object System.Collections.Generic.List[object]
    $unresolved = New-Object System.Collections.Generic.List[object]
    $skipped = 0; $merged = 0
    $scoped = New-Object System.Collections.Generic.List[object]
    $catchAll = $null
    $k = 0
    # Пріоритет - за ПОЗИЦІЄЮ правила серед доступних правил пакета (а не серед перетворених): тоді перетворення раніше
    # відкинутого правила (з'явилась колонка в шаблоні) не зсуває пріоритети вже записаних правил.
    foreach ($c in @($Converted)) {
        if (-not [bool]$c.Rule.isAvailable) { $skipped++; continue }
        $k++
        if (-not $c.Result.Ok) { [void]$unresolved.Add([pscustomobject]@{ Code = [string]$c.Rule.code; Table = [string]$c.Rule.table; Reasons = @($c.Result.Reasons) }); continue }
        if ($c.Result.CatchAll -and $c.Result.MatchJson -eq '{}') {
            if ($null -eq $catchAll) { $catchAll = [pscustomobject]@{ Code = [string]$c.Rule.code; MatchJson = '{}'; Priority = 0; Table = [string]$c.Rule.table; Source = 1 } }
            else { $catchAll.Source++; $merged++ }
            continue
        }
        $entry = [pscustomobject]@{ Code = [string]$c.Rule.code; MatchJson = [string]$c.Result.MatchJson; Priority = $k * 10; Table = [string]$c.Rule.table; Source = 1 }
        if ($c.Result.PSObject.Properties['TableScopedCatchAll'] -and [bool]$c.Result.TableScopedCatchAll) { [void]$scoped.Add($entry); continue }
        [void]$plan.Add($entry)
    }
    # «Уся таблиця» з таблицею-обмеженням: після всіх умовних правил (нижчий пріоритет), кожне зі своїм пріоритетом; не зливаються.
    $n = 0
    foreach ($sc in $scoped) { $n++; $sc.Priority = ($k + $n) * 10; [void]$plan.Add($sc) }
    if ($null -ne $catchAll) { $catchAll.Priority = ($k + $n + 1) * 10; [void]$plan.Add($catchAll) }
    return [pscustomobject]@{ Rules = $plan.ToArray(); Unresolved = $unresolved.ToArray(); Skipped = $skipped; Merged = $merged }
}

function ConvertTo-LandLegacyPredicateKey([string]$MatchJson) {
    <# Нормалізований ключ предиката для порівняння наборів: пари `колонка=значення`, відсортовані. #>
    if ([string]::IsNullOrWhiteSpace($MatchJson)) { return '' }
    $o = $MatchJson | ConvertFrom-Json
    $pairs = @($o.PSObject.Properties | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" })
    return ($pairs -join ';')
}
