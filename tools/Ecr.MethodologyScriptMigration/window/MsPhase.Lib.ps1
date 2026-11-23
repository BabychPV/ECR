# tools/Ecr.MethodologyScriptMigration/window/MsPhase.Lib.ps1
# ✎ D-571 (P-1123-4): чиста логіка фази вікна W15z (без бази й API) — щоб її перевіряли тести Architecture.
#
#   $MsPermanentExclusions   - методології, які НЕ переносяться ніколи (вода: рахується формулами таблиці, рішення людини 23.11);
#   Get-MsUnitsStillNeeded   - які накладки land-script-units.json ще потрібні, коли шаблон Land / константи вже несуть одиниці й міст;
#   Get-MsSwitchPlan         - групи перемикання за джерелом доказу (live / еталон ECR / золотий набір) і що лишається на Formulas.

# Вода ECW_C09_02_01: «Вода має прості формули тому ми їх рахуємо в таблиці і все» (людина 23.11, P-1123-4) — методологію не
# переносять і не перемикають; колонки T7b_00 рахує формула шаблону (C·V/1e6, B26 7b).
$script:MsPermanentExclusions = @('ECW_C09_02_01')

function Get-MsUnitsStillNeeded {
    <#
    .SYNOPSIS
        Накладки land-script-units.json, яких шаблон / версія ще НЕ мають. Повертає @{ Units = <той самий формат>; Skipped = @(...) }.
    .PARAMETER Facts
        @{ ColumnUnits = @{ '<методологія>|<аргумент>' = @('<код одиниці кожної прив''язаної колонки з цим кодом>') };
           ArgumentColumns = @{ '<методологія>|<аргумент>' = $true, якщо колонка-аргумент є в КОЖНІЙ прив'язаній таблиці, де є замінник };
           ConstantUnits = @{ '<методологія>|<константа>' = '<код одиниці константи чинної версії>' } }
    .DESCRIPTION
        Аргумент: накладка зайва, коли КОЖНА прив'язана колонка з цим кодом має саме цю одиницю. Джерело аргументу (міст): зайве, коли
        колонка-аргумент є в шаблоні (Land 1.0.20.0: T9_07.Land_Measure_SubstanceConsumption). Константа: зайва, коли константа чинної
        версії має цю одиницю. Решта лишається — накладка застосовується, як і доти.
    #>
    param([Parameter(Mandatory)]$Units, [Parameter(Mandatory)][hashtable]$Facts)

    $skipped = New-Object System.Collections.Generic.List[string]
    $outM = [ordered]@{}
    foreach ($m in $Units.methodologies.PSObject.Properties) {
        $keep = [ordered]@{}
        foreach ($section in 'arguments', 'constants', 'argumentSources') {
            $sec = $m.Value.PSObject.Properties[$section]
            if (-not $sec -or -not $sec.Value) { continue }
            $items = [ordered]@{}
            foreach ($p in $sec.Value.PSObject.Properties) {
                $key = "$($m.Name)|$($p.Name)"
                $done = $false
                switch ($section) {
                    'arguments' {
                        $units = @($Facts.ColumnUnits[$key])
                        $done = $units.Count -gt 0 -and @($units | Where-Object { $_ -ne [string]$p.Value.unit }).Count -eq 0
                    }
                    'constants' { $done = [string]$Facts.ConstantUnits[$key] -eq [string]$p.Value.unit -and [string]$p.Value.unit -ne '' }
                    'argumentSources' { $done = [bool]$Facts.ArgumentColumns[$key] }
                }
                if ($done) { $skipped.Add("$section $key") } else { $items[$p.Name] = $p.Value }
            }
            if ($items.Count -gt 0) { $keep[$section] = [pscustomobject]$items }
        }
        if ($keep.Count -gt 0) { $outM[$m.Name] = [pscustomobject]$keep }
    }
    $copy = [ordered]@{}
    foreach ($p in $Units.PSObject.Properties) { if ($p.Name -ne 'methodologies') { $copy[$p.Name] = $p.Value } }
    $copy['methodologies'] = [pscustomobject]$outM
    return @{ Units = [pscustomobject]$copy; Skipped = @($skipped.ToArray()) }
}

function Get-MsSwitchPlan {
    <#
    .SYNOPSIS
        Групи перемикання на Script за джерелом доказу паритету і що лишається на Formulas (з причиною).
    .PARAMETER Candidates
        Версії з тіньовим текстом: @{ VersionId; Code; Version; DbHash; GoldenExpectations ('none'|'empty'|'invalid'|'ok'); Engine (0|1) }.
        'invalid' — випадок посилається на документ, якого в базі немає (ворота відмовляють «тест не має періоду»).
    .PARAMETER Live
        Доведені на даних live: @{ <versionId> = <scriptHash> }.
    .PARAMETER Reference
        Доведені на еталонних даних ECR: @{ <versionId> = <scriptHash> }.
    .PARAMETER Mismatched
        Версії з розбіжністю (будь-яким доказом) — НІКОЛИ не перемикаються (фаза вже зупинена раніше; тут — запобіжник).
    .DESCRIPTION
        Спершу золотий набір: без очікувань — Formulas (PSS-23 не пропустить). Далі джерело доказу: live -> еталон ECR -> сам золотий набір (switch проганяє його на Script). Хеш live / еталону
        мусить дорівнювати хешу в базі (доведено САМЕ цей текст). Золотий набір без очікувань — Formulas (потрібна нова версія з
        еталоном). Постійні винятки ($MsPermanentExclusions) — Formulas завжди. Уже Script — пропуск.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Candidates,
        [hashtable]$Live = @{},
        [hashtable]$Reference = @{},
        [int[]]$Mismatched = @()
    )

    $groups = [ordered]@{ live = New-Object System.Collections.Generic.List[int]; reference = New-Object System.Collections.Generic.List[int]; golden = New-Object System.Collections.Generic.List[int] }
    $stay = New-Object System.Collections.Generic.List[object]
    $already = New-Object System.Collections.Generic.List[int]
    foreach ($c in $Candidates) {
        $id = [int]$c.VersionId
        $why = $null
        if ($script:MsPermanentExclusions -contains [string]$c.Code) { $why = 'permanent exclusion (water: table formulas, P-1123-4)' }
        elseif ([int]$c.Engine -eq 1) { $already.Add($id); continue }
        elseif ($Mismatched -contains $id) { $why = 'parity mismatch' }
        # PSS-23: перемикання вимагає зеленого золотого набору з очікуваннями — без нього ворота відкотили б УСЮ групу.
        elseif ([string]$c.GoldenExpectations -eq 'empty') { $why = 'golden set without expectations (PSS-23): new version with ECR reference needed' }
        elseif ([string]$c.GoldenExpectations -eq 'invalid') { $why = 'golden set refers to a document/period that does not exist: new version with ECR reference needed' }
        elseif ([string]$c.GoldenExpectations -ne 'ok') { $why = 'no golden set (PSS-23)' }
        elseif ($Live.ContainsKey($id) -and [string]$Live[$id] -eq [string]$c.DbHash) { $groups.live.Add($id) }
        elseif ($Reference.ContainsKey($id) -and [string]$Reference[$id] -eq [string]$c.DbHash) { $groups.reference.Add($id) }
        elseif ($Live.ContainsKey($id) -or $Reference.ContainsKey($id)) { $why = 'script changed after parity (hash differs)' }
        else { $groups.golden.Add($id) }
        if ($why) { $stay.Add([pscustomobject]@{ VersionId = $id; Code = [string]$c.Code; Version = [string]$c.Version; Reason = $why }) }
    }
    return [pscustomobject]@{
        Live = @($groups.live.ToArray()); Reference = @($groups.reference.ToArray()); Golden = @($groups.golden.ToArray())
        Already = @($already.ToArray()); Stay = @($stay.ToArray())
    }
}

function ConvertTo-MsLegacyRule {
    <#
    .SYNOPSIS
        Правило старої системи (рядок dbo.AF_EmissionCalculationWork_Rules: методологія, таблиця, «P1;P2;», «V1;V2;») -> умови.
        «*» серед параметрів — «уся таблиця».
    #>
    param([Parameter(Mandatory)][string]$Methodology, [Parameter(Mandatory)][string]$Table, [string]$Parameters = '', [string]$Values = '')
    $ps = @($Parameters.TrimEnd(';') -split ';' | Where-Object { $_ })
    $vs = @($Values.TrimEnd(';') -split ';')
    $cond = [ordered]@{}
    for ($i = 0; $i -lt $ps.Count; $i++) { if ($ps[$i] -ne '*') { $cond[$ps[$i]] = [string]$(if ($i -lt $vs.Count) { $vs[$i] } else { '' }) } }
    return [pscustomobject]@{ Methodology = $Methodology; Table = $Table; Conditions = $cond; All = ($ps -contains '*') }
}

function Test-MsLegacyRule {
    <#
    .SYNOPSIS
        Чи відбирає правило старої системи рядок (точна рівність тексту кожної умови; «уся таблиця» — будь-який рядок).
    #>
    param([Parameter(Mandatory)]$Rule, [Parameter(Mandatory)][hashtable]$Row)
    if ($Rule.All) { return $true }
    if ($Rule.Conditions.Count -eq 0) { return $false }
    foreach ($k in $Rule.Conditions.Keys) { if (-not $Row.ContainsKey($k) -or [string]$Row[$k] -ne [string]$Rule.Conditions[$k]) { return $false } }
    return $true
}
