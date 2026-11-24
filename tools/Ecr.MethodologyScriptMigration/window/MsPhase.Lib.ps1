# tools/Ecr.MethodologyScriptMigration/window/MsPhase.Lib.ps1
# ✎ D-571 (P-1123-4): чиста логіка фази вікна W15z (без бази й API) — щоб її перевіряли тести Architecture.
#
#   $MsPermanentExclusions   - методології, які НЕ переносяться ніколи — ✎ P-1124-2: ПОРОЖНЬО (див. нижче);
#   Get-MsUnitsStillNeeded   - які накладки land-script-units.json ще потрібні, коли шаблон Land / константи вже несуть одиниці й міст;
#   Get-MsSwitchPlan         - групи перемикання за джерелом доказу (live / еталон ECR / золотий набір) і що лишається на Formulas;
#   Get-MsFinalizePlan       - ✎ P-1124-2: запит остаточного перенесення НЕ чинних формульних версій (доказ кожної) і що заблоковано.

# ✎ P-1124-2 (людина 24.11: «перенести систему на нове представлення без втрат … лише наш новий редактор скриптів»; вода «має бути
# вирішена, а не виключена»): постійних винятків більше немає. Вода ECW_C09_02_01 переходить на скрипт так само, як решта: доказ —
# паритет на 25 рядках старої системи (CalcResult_Emissions_Water_Land, 25/25), одиниці точні (мг/л · м3 = г; / 1e6 = т).
# Колишній виняток (P-1123-4, 23.11) спирався на те, що в T7b_00 Land 1.0.19.0 не було входів Concentration/Volume.
$script:MsPermanentExclusions = @()

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
    .PARAMETER Synthetic
        Доведені на синтетичних еталонах з покриттям гілок (P-1123-6, версії без даних live і ECR): @{ <versionId> = <scriptHash> }.
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
        [hashtable]$Synthetic = @{},
        [int[]]$Mismatched = @()
    )

    $groups = [ordered]@{ live = New-Object System.Collections.Generic.List[int]; reference = New-Object System.Collections.Generic.List[int]; synthetic = New-Object System.Collections.Generic.List[int]; golden = New-Object System.Collections.Generic.List[int] }
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
        elseif ($Synthetic.ContainsKey($id) -and [string]$Synthetic[$id] -eq [string]$c.DbHash) { $groups.synthetic.Add($id) }
        elseif ($Live.ContainsKey($id) -or $Reference.ContainsKey($id) -or $Synthetic.ContainsKey($id)) { $why = 'script changed after parity (hash differs)' }
        else { $groups.golden.Add($id) }
        if ($why) { $stay.Add([pscustomobject]@{ VersionId = $id; Code = [string]$c.Code; Version = [string]$c.Version; Reason = $why }) }
    }
    return [pscustomobject]@{
        Live = @($groups.live.ToArray()); Reference = @($groups.reference.ToArray()); Synthetic = @($groups.synthetic.ToArray()); Golden = @($groups.golden.ToArray())
        Already = @($already.ToArray()); Stay = @($stay.ToArray())
    }
}

function Get-MsFinalizePlan {
    <#
    .SYNOPSIS
        ✎ P-1124-2: тіло запиту script-migration/finalize для формульних версій, що НЕ чинні, і перелік заблокованих (з причиною).
    .PARAMETER Candidates
        Формульні версії: @{ VersionId; Code; Version; Current ($true — за неї нічого новішого не опубліковано); IsLibrary; RuleCount;
        Importers (скільки версій імпортують бібліотеку); TextHash (хеш тексту, що піде у finalize: тінь у базі або текст convert);
        Text ($null — тінь у базі; інакше текст для версії без тіні) }.
    .PARAMETER Proofs
        Паритет на рядках: @{ <versionId> = @{ Source ('reference'|'synthetic'|'legacy'); Values; Mismatches; Hash } } (для версії —
        найсильніший доказ; розбіжність у будь-якому — тут Mismatches > 0).
    .DESCRIPTION
        Ворота плану (finalize повторює їх на сервері): чинна — не тут (звичайне перемикання); розбіжність — блок; доказ мусить бути
        САМЕ цього тексту (Hash = TextHash) і мати ≥ 1 значення; без значень — лише «без правил» (RuleCount = 0, не бібліотека) або
        «бібліотека без імпортерів». Будь-що інше — блок, і версія лишається на формулах (фаза зупиняється).
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Candidates,
        [hashtable]$Proofs = @{},
        [string]$Reason = 'P-1124-2: final transfer of formula versions to ECR Script (parity proven per version)'
    )
    $entries = New-Object System.Collections.Generic.List[object]
    $blocked = New-Object System.Collections.Generic.List[object]
    foreach ($c in $Candidates) {
        $id = [int]$c.VersionId
        $p = $Proofs[$id]
        $why = $null; $proof = $null; $values = 0
        if ($c.Current) { $why = 'current version: normal switch with all gates (not finalize)' }
        elseif ($p -and [int]$p.Mismatches -gt 0) { $why = 'parity mismatch' }
        elseif ($p -and [int]$p.Values -gt 0 -and [string]$p.Hash -ne [string]$c.TextHash) { $why = 'proven text differs from the text to finalize (hash)' }
        elseif ($p -and [int]$p.Values -gt 0) { $proof = [string]$p.Source; $values = [int]$p.Values }
        elseif ($c.IsLibrary -and [int]$c.Importers -eq 0) { $proof = 'unusedLibrary' }
        elseif (-not $c.IsLibrary -and [int]$c.RuleCount -eq 0) { $proof = 'noRules' }
        else { $why = 'not proven: no compared value' }
        if (-not $c.TextHash) { $why = 'no script text (convert failed)' }
        if ($why) { $blocked.Add([pscustomobject]@{ VersionId = $id; Code = [string]$c.Code; Version = [string]$c.Version; Reason = $why }); continue }
        $e = [ordered]@{ versionId = $id; scriptHash = [string]$c.TextHash; proof = $proof; provenValues = $values }
        if ($c.Text) { $e.text = [string]$c.Text }
        $entries.Add([pscustomobject]$e)
    }
    return [pscustomobject]@{ Request = [pscustomobject]@{ reason = $Reason; entries = @($entries.ToArray()) }; Blocked = @($blocked.ToArray()) }
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

function Get-MsBranchConditions {
    <#
    .SYNOPSIS
        ✎ P-1123-6: атомарні умови гілок у виразах методології: `@X op число` (=, <>, <, <=, >, >=), `@X = 'текст'` / `<>`,
        `in(@X, 'a', 'b', ...)`. Повтори зводяться. Умови над CURRENT_SUBSTANCE() не повертаються — їх покриває цикл речовин.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Expressions)
    $seen = @{}; $out = New-Object System.Collections.Generic.List[object]
    foreach ($e in $Expressions) {
        foreach ($m in [regex]::Matches($e, '@([A-Za-z_][A-Za-z0-9_]*)\s*(>=|<=|<>|=|>|<)\s*(-?\d+(?:\.\d+)?)(?![\d.])')) {
            $text = "@$($m.Groups[1].Value) $($m.Groups[2].Value) $($m.Groups[3].Value)"
            if ($seen.ContainsKey($text)) { continue }; $seen[$text] = 1
            $out.Add([pscustomobject]@{ Text = $text; Arg = $m.Groups[1].Value; Kind = 'num'; Op = $m.Groups[2].Value; Values = @([decimal]::Parse($m.Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)) })
        }
        foreach ($m in [regex]::Matches($e, "@([A-Za-z_][A-Za-z0-9_]*)\s*(<>|=)\s*'([^']*)'")) {
            $text = "@$($m.Groups[1].Value) $($m.Groups[2].Value) '$($m.Groups[3].Value)'"
            if ($seen.ContainsKey($text)) { continue }; $seen[$text] = 1
            $out.Add([pscustomobject]@{ Text = $text; Arg = $m.Groups[1].Value; Kind = 'str'; Op = $m.Groups[2].Value; Values = @($m.Groups[3].Value) })
        }
        foreach ($m in [regex]::Matches($e, "in\(\s*@([A-Za-z_][A-Za-z0-9_]*)\s*,\s*((?:'[^']*'\s*,?\s*)+)\)")) {
            $items = @([regex]::Matches($m.Groups[2].Value, "'([^']*)'") | ForEach-Object { $_.Groups[1].Value })
            $text = "in(@$($m.Groups[1].Value), $(@($items | ForEach-Object { "'$_'" }) -join ', '))"
            if ($seen.ContainsKey($text)) { continue }; $seen[$text] = 1
            $out.Add([pscustomobject]@{ Text = $text; Arg = $m.Groups[1].Value; Kind = 'in'; Op = 'in'; Values = $items })
        }
    }
    return @($out.ToArray())
}

function Test-MsBranchCondition {
    <#
    .SYNOPSIS
        Значення умови на рядку: $true / $false / 'null' (аргументу немає).
    #>
    param([Parameter(Mandatory)]$Condition, [Parameter(Mandatory)]$Args)
    $a = $Args[$Condition.Arg]
    if ($null -eq $a) { return 'null' }
    if ($Condition.Kind -eq 'num') {
        if (-not $a.ContainsKey('value') -or $null -eq $a.value) { return 'null' }
        $x = [decimal]$a.value; $y = [decimal]$Condition.Values[0]
        switch ($Condition.Op) { '=' { return $x -eq $y } '<>' { return $x -ne $y } '<' { return $x -lt $y } '<=' { return $x -le $y } '>' { return $x -gt $y } '>=' { return $x -ge $y } }
    }
    $s = if ($a.ContainsKey('valueString')) { [string]$a.valueString } else { $null }
    if ($null -eq $s) { return 'null' }
    if ($Condition.Kind -eq 'in') { return ($Condition.Values -contains $s) }
    if ($Condition.Op -eq '=') { return $s -eq [string]$Condition.Values[0] } else { return $s -ne [string]$Condition.Values[0] }
}

function New-MsSyntheticRows {
    <#
    .SYNOPSIS
        Синтетичні рядки: по рядку на правило (колонки правила = його значення) і варіації ОДНОГО аргументу від бази першого правила:
        для числових умов — літерал, ±1, 0 і null; для текстових / in — кожне значення списку й значення поза ним; для кожного
        числового аргументу — 0 і null.
        База: число 12.5 (місяць CalculationMonth — 6), текстовий аргумент з умов — перше значення умови.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Arguments,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Conditions,
        [Parameter(Mandatory)][object[]]$RuleArgs,
        [int]$MaxRows = 80
    )
    $textArgs = @{}; foreach ($c in $Conditions) { if ($c.Kind -ne 'num' -and -not $textArgs.ContainsKey($c.Arg)) { $textArgs[$c.Arg] = [string]$c.Values[0] } }
    function New-Base($rule) {
        $b = [ordered]@{}
        foreach ($a in ($Arguments | Sort-Object)) {
            if ($textArgs.ContainsKey($a)) { $b[$a] = @{ valueString = $textArgs[$a] } }
            elseif ($a -eq 'CalculationMonth') { $b[$a] = @{ value = [decimal]6 } }
            else { $b[$a] = @{ value = [decimal]12.5 } }
        }
        foreach ($k in $rule.Args.Keys) { $b[$k] = $rule.Args[$k] }
        return $b
    }
    $rows = New-Object System.Collections.Generic.List[object]; $sig = @{}
    function Add-Row([string]$key, $args0, $rule) {
        $s = (@($args0.Keys | Sort-Object | ForEach-Object { $v = $args0[$_]; if ($null -eq $v) { "$_=null" } else { "$_=" + (@($v.Keys | Sort-Object | ForEach-Object { "${_}:$($v[$_])" }) -join ',') } }) -join '|')
        if ($sig.ContainsKey($s) -or $rows.Count -ge $MaxRows) { return }
        $sig[$s] = 1
        $rows.Add([pscustomobject]@{ Key = $key; Args = $args0; Rule = $rule })
    }
    $i = 0
    foreach ($r in $RuleArgs) { $i++; Add-Row ('SYN_R{0:00}' -f $i) (New-Base $r) $r.Code }
    $first = $RuleArgs[0]; $n = 0
    foreach ($c in $Conditions) {
        $vals = New-Object System.Collections.Generic.List[object]
        if ($c.Kind -eq 'num') {
            $l = [decimal]$c.Values[0]
            foreach ($v in @($l, ($l - 1), ($l + 1), [decimal]0)) { $vals.Add(@{ value = $v }) }
            $vals.Add($null)
        } else {
            foreach ($v in $c.Values) { $vals.Add(@{ valueString = [string]$v }) }
            $vals.Add(@{ valueString = 'ZZZ-OUT' }); $vals.Add($null)
        }
        foreach ($v in $vals) {
            $b = New-Base $first
            $b[$c.Arg] = $v
            # Псевдонім NAE: колонка правила й аргумент мають однакове значення.
            if ($c.Arg -eq 'Land_NAE' -and $b.Contains('IDCodeNAEProject')) { $b['IDCodeNAEProject'] = $v }
            $n++; Add-Row ('SYN_V{0:00}' -f $n) $b $null
        }
    }
    # Нуль і null КОЖНОГО числового аргументу (ділення на нуль, відсутній вхід), не лише тих, що в умовах.
    foreach ($a in ($Arguments | Sort-Object)) {
        if ($textArgs.ContainsKey($a) -or $first.Args.Contains($a)) { continue }
        foreach ($v in @(@{ value = [decimal]0 }, $null)) { $b = New-Base $first; $b[$a] = $v; $n++; Add-Row ('SYN_V{0:00}' -f $n) $b $null }
    }
    return @($rows.ToArray())
}

function Get-MsBranchCoverage {
    <#
    .SYNOPSIS
        Покриття: правило — скільки рядків побудовано під нього; умова — скільки рядків, де вона істинна / хибна / null.
        Непокрите: правило без рядка; умова без істинного або без хибного рядка.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Conditions, [Parameter(Mandatory)][object[]]$Rows, [Parameter(Mandatory)][string[]]$RuleCodes)
    $rules = @($RuleCodes | ForEach-Object { $c = $_; [ordered]@{ rule = $c; rows = @($Rows | Where-Object { $_.Rule -eq $c }).Count } })
    $conds = @($Conditions | ForEach-Object {
            $c = $_; $t = 0; $f = 0; $n = 0
            foreach ($r in $Rows) { $v = Test-MsBranchCondition -Condition $c -Args $r.Args; if ($v -is [string]) { $n++ } elseif ($v) { $t++ } else { $f++ } }
            [ordered]@{ condition = $c.Text; true = $t; false = $f; null = $n; covered = ($t -gt 0 -and $f -gt 0) }
        })
    $unc = @(@($rules | Where-Object { $_.rows -eq 0 } | ForEach-Object { "rule $($_.rule)" }) + @($conds | Where-Object { -not $_.covered } | ForEach-Object { "condition $($_.condition)" }))
    return [pscustomobject]@{ Rules = $rules; Conditions = $conds; Uncovered = $unc }
}

function Merge-MsGoldenPlan {
    <#
    .SYNOPSIS
        Золоті версії шляху API (без -CopyAsUser): пропозиції ECR/синтетичні + позичені правила, ОДИН запис на версію.
        Повертає записи @{ VersionId = [int]; P = <пропозиція або $null>; R = @(<правила>) } у порядку першої появи.
    #>
    param([object[]]$Proposals = @(), [object[]]$Borrowed = @())
    # ⛔ Ключ — РЯДОК: [ordered]@{} (OrderedDictionary) з ключем [int] бере ПОЗИЦІЙНИЙ індексатор — запис кидає
    #   «out of the range … index», читання мовчки дає $null (репетиція b11 8e58d19b8, шлях API).
    $byVid = [ordered]@{}
    foreach ($x in @($Proposals)) { $byVid["$([int]$x.versionId)"] = @{ VersionId = [int]$x.versionId; P = $x; R = @() } }
    foreach ($x in @($Borrowed)) {
        if (@($x.rules).Count -gt 0) {
            $k = "$([int]$x.versionId)"
            if (-not $byVid.Contains($k)) { $byVid[$k] = @{ VersionId = [int]$x.versionId; P = $null; R = @() } }
            $byVid[$k].R = @($x.rules)
        }
    }
    return @($byVid.Values)
}