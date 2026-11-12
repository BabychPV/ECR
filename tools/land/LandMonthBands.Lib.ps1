# tools/land/LandMonthBands.Lib.ps1
# Місячні смуги (групи заголовків колонок) для Land: чиста логіка плану + виконання через публічний API.
# Спільна для Add-LandMonthGroups.ps1 (опублікована версія, презентаційний шар) і Build-LandTemplate.ps1 (нові версії).
# Сумісна з Windows PowerShell 5.1 і PowerShell 7. Секретів не містить.

function Read-LandMonthBandSource {
    param([Parameter(Mandatory)][string]$Path)
    $full = (Resolve-Path -LiteralPath $Path).Path   # .NET не знає поточного каталогу PowerShell
    $src = [IO.File]::ReadAllText($full, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($null -eq $src.months -or @($src.months).Count -ne 12) { throw "Джерело смуг $Path : потрібно рівно 12 місяців." }
    return $src
}

function Compare-LandBandColumn {
    # Порядок колонок той самий, що в ColumnHeaderGroupRules.Validate: Ordinal, потім Code ordinal-порівнянням.
    param($Left, $Right)
    $x = ([int]$Left.Ordinal).CompareTo([int]$Right.Ordinal)
    if ($x -ne 0) { return $x }
    return [string]::CompareOrdinal([string]$Left.Code, [string]$Right.Code)
}

function Get-LandBandOrderedColumns {
    param([object[]]$Columns)
    $list = New-Object 'System.Collections.Generic.List[object]'
    foreach ($c in @($Columns)) { $list.Add($c) }
    $list.Sort([Comparison[object]] { param($first, $second) Compare-LandBandColumn $first $second })
    return , $list.ToArray()
}

function Get-LandMonthBlocks {
    <#
    .SYNOPSIS
        Суміжні блоки місячних колонок у порядку сервера: максимальний пробіг колонок з однаковим MonthNumber.
        Немісячна колонка або колонка іншого місяця розриває блок. Повертає @{ Month; Start; End; Columns } (позиції 0-based).
    #>
    param([object[]]$Sequence)
    $blocks = @()
    $cur = $null
    for ($i = 0; $i -lt $Sequence.Count; $i++) {
        $mn = $Sequence[$i].MonthNumber
        $mn = if ($null -eq $mn -or [string]$mn -eq '') { 0 } else { [int]$mn }
        if ($mn -ge 1 -and $mn -le 12) {
            if ($null -ne $cur -and $cur.Month -eq $mn -and $cur.End -eq $i - 1) { $cur.End = $i; $cur.Columns += [string]$Sequence[$i].Code; continue }
            if ($null -ne $cur) { $blocks += [pscustomobject]$cur }
            $cur = @{ Month = $mn; Start = $i; End = $i; Columns = @([string]$Sequence[$i].Code) }
        }
        elseif ($null -ne $cur) { $blocks += [pscustomobject]$cur; $cur = $null }
    }
    if ($null -ne $cur) { $blocks += [pscustomobject]$cur }
    return , $blocks
}

function Get-LandMonthBandPlan {
    <#
    .SYNOPSIS
        План смуг для ОДНІЄЇ таблиці. Чиста функція (без мережі).
    .PARAMETER Columns
        Живі колонки: Code, Ordinal, MonthNumber ($null — не місячна), GroupCode (поточна група або $null).
    .PARAMETER ExistingGroups
        Поточні групи таблиці: Code, ParentCode, Ordinal, En, Ru.
    .PARAMETER OutputGroups
        LAND-YEAR (L4, рішення 12.11 п. 1): групи «вихід -> 12 місячних колонок» (Code з префіксом MB_O_, En, Ru, Columns).
        Їхні колонки не утворюють місячних смуг; група стає під найглибшу батьківську смугу, що її охоплює.
    .OUTPUTS
        Skip (немає місячних колонок — таблицю не чіпати), Errors (таблицю пропущено), Warnings, Groups (повне дерево для PUT,
        чужі групи збережено), GroupsChanged, Changes (колонка: From -> To), MonthGroups, ParentGroups, AssignedColumns.
    #>
    param([Parameter(Mandatory)][string]$TableCode, [object[]]$Columns, [object[]]$ExistingGroups, [Parameter(Mandatory)]$Source, [object[]]$OutputGroups = @())

    $prefix = 'MB_'
    $plan = [ordered]@{ Table = $TableCode; Skip = $false; Errors = @(); Warnings = @(); Groups = @(); GroupsChanged = $false
        Changes = @(); MonthGroups = 0; ParentGroups = 0; OutputGroups = 0; AssignedColumns = 0; DuplicateOrdinals = @() }
    $ordered = Get-LandBandOrderedColumns $Columns

    # дублікати Ordinal: сервер розв'язує їх за Code; звітуємо, нічого не переставляємо
    $byOrd = @{}
    foreach ($c in $ordered) { $k = [int]$c.Ordinal; if (-not $byOrd.ContainsKey($k)) { $byOrd[$k] = @() }; $byOrd[$k] += [string]$c.Code }
    foreach ($k in ($byOrd.Keys | Sort-Object)) {
        if (@($byOrd[$k]).Count -gt 1) {
            $plan.DuplicateOrdinals += ('{0} Ordinal {1}: {2}' -f $TableCode, $k, (@($byOrd[$k]) -join ', '))
        }
    }

    $outGroups = @($OutputGroups | Where-Object { $_ })
    $inOut = @{}
    foreach ($g in $outGroups) { foreach ($c in @($g.Columns)) { $inOut[[string]$c] = [string]$g.Code } }
    $blockSeq = @($ordered | ForEach-Object { if ($inOut.ContainsKey([string]$_.Code)) { [pscustomobject]@{ Code = $_.Code; MonthNumber = $null } } else { $_ } })
    $blocks = Get-LandMonthBlocks $blockSeq
    if ($blocks.Count -eq 0 -and $outGroups.Count -eq 0) { $plan.Skip = $true; return [pscustomobject]$plan }
    foreach ($d in $plan.DuplicateOrdinals) { $plan.Warnings += "дубль Ordinal (сервер упорядковує за Code, не переставляю): $d" }

    $pos = @{}
    for ($i = 0; $i -lt $ordered.Count; $i++) { $pos[[string]$ordered[$i].Code] = $i }
    $names = @{}
    foreach ($m in @($Source.months)) { $names[[int]$m.month] = $m }

    # батьківські смуги з джерела (діапазон колонок за кодами)
    $parents = @()
    foreach ($p in @($Source.parents | Where-Object { $_.table -eq $TableCode })) {
        $from = [string]$p.from; $to = [string]$p.to
        if (-not $pos.ContainsKey($from) -or -not $pos.ContainsKey($to)) { $plan.Warnings += "смугу $($p.code) пропущено: немає колонки $from або $to"; continue }
        $s = [int]$pos[$from]; $e = [int]$pos[$to]
        if ($e -lt $s) { $plan.Warnings += "смугу $($p.code) пропущено: $to стоїть перед $from"; continue }
        $partial = @($blocks | Where-Object { ($_.Start -lt $s -and $_.End -ge $s) -or ($_.Start -le $e -and $_.End -gt $e) })
        if ($partial.Count -gt 0) { $plan.Warnings += "смугу $($p.code) пропущено: місячний блок перетинає її межу"; continue }
        # Смуга без жодного прив'язаного місячного блоку не ставиться: у 1.0.14.0 блоки RH аркушів 9/10/11 ще без місяця,
        # і квартал інакше згрупував би саме ті колонки (JanuaryRHDuringExceeding…), яких ця правка не чіпає.
        if (@($blocks | Where-Object { $_.Start -ge $s -and $_.End -le $e }).Count -eq 0) { continue }
        $parents += [pscustomobject]@{ Code = [string]$p.code; Parent = $(if ($p.parent) { [string]$p.parent } else { $null }); En = [string]$p.en
            Ru = $(if ($p.ru) { [string]$p.ru } else { $null }); Start = $s; End = $e }
    }
    # батько має існувати й охоплювати дитину; інакше смуга стає коренем
    $pc = @{}; foreach ($p in $parents) { $pc[$p.Code] = $p }
    foreach ($p in $parents) {
        if ($null -ne $p.Parent) {
            $up = $pc[$p.Parent]
            if ($null -eq $up -or $up.Start -gt $p.Start -or $up.End -lt $p.End) { $plan.Warnings += "смуга $($p.Code): батька $($p.Parent) немає або він не охоплює її — корінь"; $p.Parent = $null }
        }
    }
    function Get-Depth($groupCode) { $d = 0; $x = $pc[$groupCode]; while ($null -ne $x) { $d++; $x = if ($x.Parent) { $pc[$x.Parent] } else { $null } }; return $d }
    function Get-Deepest([int]$i) {
        $best = $null; $bd = -1
        foreach ($p in $parents) { if ($i -ge $p.Start -and $i -le $p.End) { $d = Get-Depth $p.Code; if ($d -gt $bd) { $best = $p; $bd = $d } } }
        return $best
    }

    # бажане призначення колонок і групи
    $want = @{}      # code -> group code
    $groups = @()    # MB-групи (code, parent, en, ru, firstPos)
    $seen = @{}
    foreach ($p in $parents) {
        if ((Get-Depth $p.Code) -ge 3) { $plan.Errors += "смуга $($p.Code): місячна група вийшла б глибше 3 рівнів"; continue }
        $groups += [pscustomobject]@{ Code = $p.Code; Parent = $p.Parent; En = $p.En; Ru = $p.Ru; First = $p.Start }
    }
    $plan.ParentGroups = $parents.Count
    foreach ($b in $blocks) {
        $n = if ($seen.ContainsKey($b.Month)) { $seen[$b.Month] + 1 } else { 1 }
        $seen[$b.Month] = $n
        $code = '{0}M{1:00}' -f $prefix, $b.Month
        if ($n -gt 1) { $code = "${code}_$n" }
        $par = Get-Deepest $b.Start
        $label = $names[[int]$b.Month]
        $groups += [pscustomobject]@{ Code = $code; Parent = $(if ($par) { $par.Code } else { $null }); En = [string]$label.en; Ru = [string]$label.ru; First = $b.Start }
        foreach ($c in $b.Columns) { $want[$c] = $code }
    }
    foreach ($g in $outGroups) {
        $gp = @(@($g.Columns) | Where-Object { $pos.ContainsKey([string]$_) } | ForEach-Object { [int]$pos[[string]$_] } | Sort-Object)
        if ($gp.Count -eq 0) { $plan.Warnings += "групу $($g.Code) пропущено: колонок немає в таблиці"; continue }
        if ($gp.Count -ne @($g.Columns).Count -or ($gp[-1] - $gp[0]) -ne ($gp.Count - 1)) { $plan.Errors += "група $($g.Code): колонки не суміжні або їх бракує"; continue }
        if (-not ([string]$g.Code).StartsWith($prefix)) { $plan.Errors += "група $($g.Code): код без префікса $prefix"; continue }
        $par = Get-Deepest $gp[0]
        if ($par -and (Get-Depth $par.Code) -ge 3) { $plan.Errors += "група $($g.Code): глибше 3 рівнів"; continue }
        $groups += [pscustomobject]@{ Code = [string]$g.Code; Parent = $(if ($par) { $par.Code } else { $null }); En = [string]$g.En; Ru = $(if ($g.Ru) { [string]$g.Ru } else { $null }); First = $gp[0] }
        foreach ($c in @($g.Columns)) { $want[[string]$c] = [string]$g.Code }
        $plan.OutputGroups++
    }
    for ($i = 0; $i -lt $ordered.Count; $i++) {
        $c = [string]$ordered[$i].Code
        if ($want.ContainsKey($c)) { continue }
        $par = Get-Deepest $i
        if ($null -ne $par) { $want[$c] = $par.Code }
    }
    $plan.MonthGroups = $blocks.Count
    $plan.AssignedColumns = $want.Count

    # чужа група на колонці, яку треба призначити, — конфлікт: таблицю пропускаємо
    foreach ($c in $ordered) {
        $cur = if ($c.GroupCode) { [string]$c.GroupCode } else { $null }
        $code = [string]$c.Code
        if ($want.ContainsKey($code) -and $null -ne $cur -and -not $cur.StartsWith($prefix) ) {
            $plan.Errors += "колонка $code уже в чужій групі $cur"
        }
    }
    if ($plan.Errors.Count -gt 0) { return [pscustomobject]$plan }

    # порядок груп: за першою колонкою; ordinal 1..n
    $sorted = @($groups | Sort-Object First, @{ e = { if ($_.Parent) { 1 } else { 0 } } })
    $desired = @(); $o = 0
    foreach ($g in $sorted) {
        $o++
        $lab = [ordered]@{ en = $g.En }; if ($g.Ru) { $lab.ru = $g.Ru }
        $desired += [pscustomobject]@{ code = $g.Code; parentCode = $g.Parent; ordinal = $o; labelL10n = $lab }
    }
    $foreign = @($ExistingGroups | Where-Object { -not ([string]$_.Code).StartsWith($prefix) } | ForEach-Object {
            $lab = [ordered]@{}; if ($_.En) { $lab.en = [string]$_.En }; if ($_.Ru) { $lab.ru = [string]$_.Ru }
            [pscustomobject]@{ code = [string]$_.Code; parentCode = $_.ParentCode; ordinal = [int]$_.Ordinal; labelL10n = $lab } })
    $plan.Groups = @($foreign + $desired)

    $mine = @($ExistingGroups | Where-Object { ([string]$_.Code).StartsWith($prefix) })
    $sig = { param($sCode, $sParent, $sOrd, $sEn, $sRu) '{0}|{1}|{2}|{3}|{4}' -f $sCode, $sParent, $sOrd, $sEn, $sRu }
    $a = @($desired | ForEach-Object { & $sig $_.code $_.parentCode $_.ordinal $_.labelL10n.en $(if ($_.labelL10n.Contains('ru')) { $_.labelL10n.ru } else { '' }) }) | Sort-Object
    $b2 = @($mine | ForEach-Object { & $sig $_.Code $_.ParentCode ([int]$_.Ordinal) $_.En $(if ($_.Ru) { $_.Ru } else { '' }) }) | Sort-Object
    $plan.GroupsChanged = ((@($a) -join "`n") -cne (@($b2) -join "`n"))

    foreach ($c in $ordered) {
        $code = [string]$c.Code
        $cur = if ($c.GroupCode) { [string]$c.GroupCode } else { $null }
        $to = if ($want.ContainsKey($code)) { $want[$code] } elseif ($null -ne $cur -and $cur.StartsWith($prefix)) { $null } else { $cur }
        if ($to -cne $cur) { $plan.Changes += [pscustomobject]@{ Column = $code; From = $cur; To = $to } }
    }
    return [pscustomobject]$plan
}

function Get-LandBandTables {
    <#
    .SYNOPSIS
        Читає версію через API одним GET …/structure (групи, коди, Ordinal, isMonthColumn/monthNumber); на старішому
        сервері без цих полів — ще GET …/columns/{code} на колонку.
        Повертає список @{ Sheet; Table; TableId; Columns; Groups; ColumnIds }.
    #>
    param([Parameter(Mandatory)][int]$VersionId, [string[]]$OnlyTables)
    $st = Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$VersionId/structure"
    if ($st.Status -ne 200) { throw "GET structure версії $VersionId : HTTP $($st.Status)" }
    $out = @()
    foreach ($sh in @($st.Json.sheets)) {
        foreach ($tb in @($sh.tables)) {
            if ($OnlyTables -and $OnlyTables -notcontains [string]$tb.code) { continue }
            $gById = @{}
            foreach ($g in @($tb.headerGroups)) { $gById[[int]$g.id] = $g }
            $groups = @()
            foreach ($g in @($tb.headerGroups)) {
                $v = $null; if ($g.labelL10n -and $g.labelL10n.PSObject.Properties['values']) { $v = $g.labelL10n.values }
                $en = $null; $ru = $null
                if ($v) { if ($v.PSObject.Properties['en']) { $en = [string]$v.en }; if ($v.PSObject.Properties['ru']) { $ru = [string]$v.ru } }
                $pcode = $null; if ($null -ne $g.parentId -and $gById.ContainsKey([int]$g.parentId)) { $pcode = [string]$gById[[int]$g.parentId].code }
                $groups += [pscustomobject]@{ Code = [string]$g.code; ParentCode = $pcode; Ordinal = [int]$g.ordinal; En = $en; Ru = $ru; Id = [int]$g.id }
            }
            $cols = @(); $ids = @{}
            foreach ($c in @($tb.columns)) {
                # D-474: сервер з місячними полями у структурі — без окремих запитів; старіший (властивості isMonthColumn
                # у відповіді немає взагалі) — запит на колонку, як раніше (keepalive EcrApi тримає сеанс).
                $src = $c
                if (-not $c.PSObject.Properties['isMonthColumn']) {
                    $r = Invoke-Ecr -Method GET -Path ("/api/v1/template-versions/$VersionId/tables/$($tb.id)/columns/" + [uri]::EscapeDataString([string]$c.code))
                    if ($r.Status -ne 200) { throw "GET колонки $($tb.code)/$($c.code): HTTP $($r.Status)" }
                    $src = $r.Json
                }
                $mn = $null; if ($src.isMonthColumn -and $null -ne $src.monthNumber) { $mn = [int]$src.monthNumber }
                $gc = $null; if ($null -ne $c.headerGroupId -and $gById.ContainsKey([int]$c.headerGroupId)) { $gc = [string]$gById[[int]$c.headerGroupId].code }
                $cols += [pscustomobject]@{ Code = [string]$c.code; Ordinal = [int]$c.ordinal; MonthNumber = $mn; GroupCode = $gc; Hidden = [bool]$c.isHidden }
                $ids[[string]$c.code] = [int]$c.id
            }
            $out += [pscustomobject]@{ Sheet = [string]$sh.code; Table = [string]$tb.code; TableId = [int]$tb.id; Columns = $cols; Groups = $groups; ColumnIds = $ids }
        }
    }
    return , $out
}

function Invoke-LandMonthBands {
    <#
    .SYNOPSIS
        План і (без -WhatIfOnly) застосування смуг до версії: на таблицю ОДИН PUT …/header-groups (лише якщо дерево змінюється)
        і ОДИН PATCH …/presentation (лише змінені колонки). Повтор — 0 змін. Повертає підсумок.
    #>
    param([Parameter(Mandatory)][int]$VersionId, [Parameter(Mandatory)]$Source, [switch]$WhatIfOnly, [string[]]$OnlyTables,
        [scriptblock]$Log = { param($msg) Write-Host $msg }, [hashtable]$OutputGroupsByTable = @{})
    $sum = [ordered]@{ Tables = 0; TablesWithMonths = 0; TablesChanged = 0; TablesSkippedErrors = 0; MonthGroups = 0; ParentGroups = 0
        AssignedColumns = 0; ColumnChanges = 0; Puts = 0; Patches = 0; DuplicateOrdinals = @(); Errors = @() }
    foreach ($t in (Get-LandBandTables -VersionId $VersionId -OnlyTables $OnlyTables)) {
        $sum.Tables++
        $p = Get-LandMonthBandPlan -TableCode $t.Table -Columns $t.Columns -ExistingGroups $t.Groups -Source $Source -OutputGroups @($OutputGroupsByTable[$t.Table])
        $sum.DuplicateOrdinals += @($p.DuplicateOrdinals | ForEach-Object { "$($t.Sheet)/$_" })
        if ($p.Skip) { continue }
        $sum.TablesWithMonths++
        foreach ($w in $p.Warnings) { & $Log ("  WARN {0}/{1}: {2}" -f $t.Sheet, $t.Table, $w) }
        if ($p.Errors.Count -gt 0) {
            $sum.TablesSkippedErrors++
            foreach ($e in $p.Errors) { $sum.Errors += "$($t.Sheet)/$($t.Table): $e"; & $Log ("  ERROR {0}/{1}: {2} — таблицю пропущено" -f $t.Sheet, $t.Table, $e) }
            continue
        }
        $sum.MonthGroups += $p.MonthGroups; $sum.ParentGroups += $p.ParentGroups; $sum.AssignedColumns += $p.AssignedColumns
        $sum.ColumnChanges += @($p.Changes).Count
        $changed = $p.GroupsChanged -or @($p.Changes).Count -gt 0
        & $Log ("  {0}/{1}: місячних груп {2}, батьківських {3}, колонок у групах {4}; змін дерева {5}, змін колонок {6}" -f $t.Sheet, $t.Table, $p.MonthGroups, $p.ParentGroups, $p.AssignedColumns, $(if ($p.GroupsChanged) { 1 } else { 0 }), @($p.Changes).Count)
        if (-not $changed) { continue }
        $sum.TablesChanged++
        if ($WhatIfOnly) { continue }
        $gid = @{}
        foreach ($g in $t.Groups) { $gid[$g.Code] = $g.Id }
        if ($p.GroupsChanged) {
            $body = @{ groups = @($p.Groups | ForEach-Object { @{ code = $_.code; parentCode = $_.parentCode; ordinal = $_.ordinal; labelL10n = $_.labelL10n } }) }
            $r = Invoke-Ecr -Method PUT -Path "/api/v1/template-versions/$VersionId/tables/$($t.TableId)/header-groups" -Body $body
            if ($r.Status -ne 200) { throw "PUT header-groups $($t.Sheet)/$($t.Table): HTTP $($r.Status) $(Get-EcrErrorCode $r) $($r.Text)" }
            $sum.Puts++
            $gid = @{}
            foreach ($g in @($r.Json.groups)) { $gid[[string]$g.code] = [int]$g.id }
        }
        if (@($p.Changes).Count -gt 0) {
            $patch = @($p.Changes | ForEach-Object {
                    @{ entityType = 'ColumnDef'; entityId = [int]$t.ColumnIds[$_.Column]; field = 'HeaderGroupId'
                        value = $(if ($null -eq $_.To) { $null } else { [string]$gid[$_.To] }) } })
            $r = Invoke-Ecr -Method PATCH -Path "/api/v1/template-versions/$VersionId/presentation" -Body $patch
            if ($r.Status -ne 200) { throw "PATCH presentation $($t.Sheet)/$($t.Table): HTTP $($r.Status) $(Get-EcrErrorCode $r) $($r.Text)" }
            $sum.Patches++
        }
    }
    return [pscustomobject]$sum
}
