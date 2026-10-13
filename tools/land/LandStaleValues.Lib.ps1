# Спільні функції перенесення значень зі застарілих таблиць Land (T9a_03/06/07) у нові (T9a_17a/21a/21b) - D-1.
# Після migrate-version БЕЗ мапи колонок і БЕЗ -PruneStaleTables старі таблиці лишаються в документах разом зі значеннями.
# Ця бібліотека лише читає зрізи через REST і (з -Execute) пише PATCH /documents/{id}/cells у НОВУ таблицю.
# Значення клітинок не друкуються: у вивід потрапляють лише лічильники й коди таблиць/колонок.
# Dot-source разом з EcrApi.ps1: . .\EcrApi.ps1; . .\LandStaleValues.Lib.ps1

function Get-LandProp {
    <# Безпечне читання властивості JSON-об'єкта (немає властивості або об'єкта - $null; безпечно під StrictMode). #>
    param($Obj, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Obj) { return $null }
    $p = $Obj.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Read-LandStaleMap {
    <#
    .SYNOPSIS
        Мапа перейменувань таблиць (tools/land/rename-maps/*.json) -> список записів From/To/Sheet/Columns.
    .DESCRIPTION
        Формат запису збігається з tableRenames migrate-version: fromTableCode, toTableCode, sheetCode (необов'язково),
        columns[{fromColumnCode,toColumnCode}] (необов'язково; без нього колонки збігаються за кодами).
    #>
    param([Parameter(Mandatory)][string]$Path)
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($e in @($raw)) {
        if ($null -eq $e) { continue }
        $from = [string](Get-LandProp $e 'fromTableCode')
        $to = [string](Get-LandProp $e 'toTableCode')
        if ([string]::IsNullOrWhiteSpace($from) -or [string]::IsNullOrWhiteSpace($to)) {
            throw "Мапа ${Path}: запис без fromTableCode/toTableCode."
        }
        $cols = @{}
        foreach ($c in @(Get-LandProp $e 'columns')) {
            if ($null -eq $c) { continue }
            $cf = [string](Get-LandProp $c 'fromColumnCode')
            $ct = [string](Get-LandProp $c 'toColumnCode')
            if ([string]::IsNullOrWhiteSpace($cf) -or [string]::IsNullOrWhiteSpace($ct)) {
                throw "Мапа ${Path}: таблиця $from має запис колонки без fromColumnCode/toColumnCode."
            }
            $cols[$cf] = $ct
        }
        $sheet = [string](Get-LandProp $e 'sheetCode')
        $out.Add([pscustomobject]@{ From = $from; To = $to; Sheet = $sheet; Columns = $cols })
    }
    return $out.ToArray()
}

function Test-LandSheetLocked {
    <# $true для стану аркуша Submitted/Approved (за іменем або числом enum DocumentStatus): запис потребує Reopen. #>
    param($State)
    if ($null -eq $State) { return $false }
    $s = ([string]$State).Trim().ToLowerInvariant()
    return ($s -eq 'submitted' -or $s -eq 'approved' -or $s -eq '1' -or $s -eq '2')
}

function Get-LandSheetStateMap {
    <# GET /documents/{id}?periodKey= -> @{ код аркуша = стан }: з sheets[] (code, state) і sheetStates. #>
    param($Document)
    $map = @{}
    $ss = Get-LandProp $Document 'sheetStates'
    if ($null -ne $ss) { foreach ($p in $ss.PSObject.Properties) { $map[[string]$p.Name] = [string]$p.Value } }
    foreach ($sh in @(Get-LandProp $Document 'sheets')) {
        if ($null -eq $sh) { continue }
        $code = [string](Get-LandProp $sh 'code')
        if (-not [string]::IsNullOrEmpty($code)) { $map[$code] = [string](Get-LandProp $sh 'state') }
    }
    return $map
}

function Test-LandValueEqual {
    <# Рівність значень комірки: числа - як decimal, решта - порядковий рядок. #>
    param($A, $B)
    if ($null -eq $A -or $null -eq $B) { return ($null -eq $A -and $null -eq $B) }
    $isNum = { param($x) ($x -is [int] -or $x -is [long] -or $x -is [double] -or $x -is [decimal] -or $x -is [single] -or $x -is [int16] -or $x -is [byte]) }
    if ((& $isNum $A) -and (& $isNum $B)) {
        try { return ([decimal]$A -eq [decimal]$B) } catch { return ([double]$A -eq [double]$B) }
    }
    return ([string]$A -ceq [string]$B)
}

function Get-LandStaleTablePlan {
    <#
    .SYNOPSIS
        План перенесення значень однієї пари таблиць (зріз старої -> зріз нової) без жодних записів.
    .DESCRIPTION
        Рядок ідентифікується rowKey (він зберігається при перенесенні: так само, як migrate-version переносить рядки),
        колонка - за кодом (або за мапою колонок запису). Правила для значення джерела (непорожнього):
          - колонки немає в новій таблиці           -> UnresolvedColumnValues (+ код у UnresolvedColumns);
          - колонка нової таблиці лише для читання  -> ReadOnlyTarget;
          - рядка немає, а нова таблиця без динамічних рядків -> UnresolvedRowValues;
          - у цілі вже є таке саме значення         -> Same (ідемпотентність);
          - у цілі інше значення                    -> Conflicts (пропуск; з -OverwriteConflicts - запис, Overwritten);
          - у цілі порожньо                         -> Moved (запис).
        Явна порожнеча в джерелі (null) значенням не вважається.
    .OUTPUTS
        pscustomobject: лічильники, UnresolvedColumns, Rows (PatchRow-и для цілі), ClearRows (що стерти в джерелі).
    #>
    param(
        [Parameter(Mandatory)]$Source,
        [Parameter(Mandatory)]$Target,
        $ColumnMap = $null,
        [bool]$AllowsDynamicRows = $true,
        [bool]$OverwriteConflicts = $false
    )
    $colRename = $ColumnMap
    if ($null -eq $colRename) { $colRename = @{} }
    $dstCols = @{}
    foreach ($c in @(Get-LandProp $Target 'columns')) {
        if ($null -eq $c) { continue }
        $dstCols[[string](Get-LandProp $c 'code')] = [bool](Get-LandProp $c 'isReadOnly')
    }
    $dstRows = @{}
    foreach ($r in @(Get-LandProp $Target 'rows')) {
        if ($null -ne $r) { $dstRows[[string](Get-LandProp $r 'rowKey')] = $r }
    }

    $plan = [pscustomobject]@{
        Values = 0; Moved = 0; Overwritten = 0; Same = 0; Conflicts = 0; ReadOnlyTarget = 0
        UnresolvedColumnValues = 0; UnresolvedRowValues = 0
        UnresolvedColumns = (New-Object System.Collections.Generic.List[string])
        Rows = (New-Object System.Collections.Generic.List[object])
        ClearRows = (New-Object System.Collections.Generic.List[object])
    }

    foreach ($sr in @(Get-LandProp $Source 'rows')) {
        if ($null -eq $sr) { continue }
        $cellsObj = Get-LandProp $sr 'cells'
        if ($null -eq $cellsObj) { continue }
        $rk = [string](Get-LandProp $sr 'rowKey')
        $dr = $null
        if ($dstRows.ContainsKey($rk)) { $dr = $dstRows[$rk] }
        $dCells = $null
        if ($null -ne $dr) { $dCells = Get-LandProp $dr 'cells' }
        $patch = New-Object System.Collections.Generic.List[object]
        $clear = New-Object System.Collections.Generic.List[string]

        foreach ($prop in $cellsObj.PSObject.Properties) {
            $val = $prop.Value
            if ($null -eq $val) { continue }
            if ($val -is [string] -and $val.Length -eq 0) { continue }
            $plan.Values++
            $scode = [string]$prop.Name
            $tcode = $scode
            if ($colRename.ContainsKey($scode)) { $tcode = [string]$colRename[$scode] }
            if (-not $dstCols.ContainsKey($tcode)) {
                $plan.UnresolvedColumnValues++
                if (-not $plan.UnresolvedColumns.Contains($tcode)) { $plan.UnresolvedColumns.Add($tcode) }
                continue
            }
            if ($dstCols[$tcode]) { $plan.ReadOnlyTarget++; continue }
            if ($null -eq $dr -and -not $AllowsDynamicRows) { $plan.UnresolvedRowValues++; continue }
            $cur = $null
            if ($null -ne $dCells) { $cur = Get-LandProp $dCells $tcode }
            if ($null -ne $cur -and -not ($cur -is [string] -and $cur.Length -eq 0)) {
                if (Test-LandValueEqual $val $cur) { $plan.Same++; $clear.Add($scode); continue }
                $plan.Conflicts++
                if (-not $OverwriteConflicts) { continue }
                $plan.Overwritten++
            }
            else { $plan.Moved++ }
            $patch.Add(@{ columnCode = $tcode; isEmpty = $false; value = $val })
            $clear.Add($scode)
        }

        if ($patch.Count -gt 0) {
            $bv = $null
            if ($null -ne $dr) { $bv = [string](Get-LandProp $dr 'rowVersion') }
            $plan.Rows.Add(@{ rowKey = $rk; baseVersion = $bv; cells = $patch.ToArray() })
        }
        if ($clear.Count -gt 0) {
            $plan.ClearRows.Add([pscustomobject]@{ RowKey = $rk; BaseVersion = [string](Get-LandProp $sr 'rowVersion'); Columns = $clear.ToArray(); Patched = ($patch.Count -gt 0) })
        }
    }
    return $plan
}

function Get-LandRetryAfter {
    param($Response)
    $raw = $null
    if ($null -ne $Response -and $null -ne $Response.Headers) {
        foreach ($k in @($Response.Headers.Keys)) { if ([string]$k -ieq 'Retry-After') { $raw = [string]$Response.Headers[$k] } }
    }
    $n = 0
    if ($raw -and [int]::TryParse($raw.Trim(), [ref]$n)) { return [Math]::Max(1, $n) }
    return 0
}

function Wait-LandStale {
    param([double]$Seconds)
    if ($Seconds -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Ceiling($Seconds * 1000)) }
}

$script:LandStaleLastCall = [DateTime]::MinValue

function Invoke-LandStaleCall {
    <# Виклик API з пейсингом (не частіше 1 виклику на PaceSeconds) і очікуванням Retry-After на 429/503. #>
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        $Body = $null,
        [double]$PaceSeconds = 2.2
    )
    $r = $null
    foreach ($try in 1..6) {
        $gap = $PaceSeconds - ([DateTime]::UtcNow - $script:LandStaleLastCall).TotalSeconds
        if ($gap -gt 0) { Wait-LandStale $gap }
        $script:LandStaleLastCall = [DateTime]::UtcNow
        $r = Invoke-Ecr -Method $Method -Path $Path -Body $Body
        if ($r.Status -ne 429 -and $r.Status -ne 503) { break }
        $ra = Get-LandRetryAfter $r
        if ($ra -le 0) { $ra = 2 * $try }
        Wait-LandStale $ra
    }
    return $r
}

function Send-LandStaleRows {
    <# PATCH cells порціями; відхилену порцію повторює по одному рядку. Повертає @{ Done; Failed; DoneKeys }. #>
    param([int]$DocumentId, [int]$PeriodKey, [long]$TableInstanceId, $Rows, [int]$ChunkRows, [double]$PaceSeconds)
    $done = 0; $failed = 0
    $doneKeys = New-Object System.Collections.Generic.HashSet[string]
    $all = $Rows.ToArray()   # List[object] (не @($Rows): PowerShell 5.1 дає «Argument types do not match»)
    for ($i = 0; $i -lt $all.Count; $i += $ChunkRows) {
        $chunk = @($all[$i..([Math]::Min($i + $ChunkRows - 1, $all.Count - 1))])
        $cellsUrl = "/api/v1/documents/$DocumentId/cells"
        $r = Invoke-LandStaleCall -Method PATCH -Path $cellsUrl -PaceSeconds $PaceSeconds -Body @{ origin = 'UserEdit'; periodKey = $PeriodKey; tableInstanceId = $TableInstanceId; rows = $chunk }
        if ($r.Status -eq 200) {
            foreach ($row in $chunk) { $done++; [void]$doneKeys.Add([string]$row.rowKey) }
            continue
        }
        foreach ($one in $chunk) {
            $r1 = Invoke-LandStaleCall -Method PATCH -Path $cellsUrl -PaceSeconds $PaceSeconds -Body @{ origin = 'UserEdit'; periodKey = $PeriodKey; tableInstanceId = $TableInstanceId; rows = @($one) }
            if ($r1.Status -eq 200) { $done++; [void]$doneKeys.Add([string]$one.rowKey) } else { $failed++ }
        }
    }
    return [pscustomobject]@{ Done = $done; Failed = $failed; DoneKeys = $doneKeys }
}

function Invoke-LandStaleMove {
    <#
    .SYNOPSIS
        Перенос значень зі застарілих таблиць у нові для заданих документів і періодів.
    .DESCRIPTION
        Фаза 1 (лише читання): для кожного документа/періоду зрізи старої й нової таблиці -> план.
        Фаза 2: перевірка станів аркушів. Якщо є що писати, а аркуш (нова таблиця; з -ClearSource і стара) у стані
        Submitted/Approved, то з -Execute запис НЕ починається взагалі (виняток: «потрібен Reopen аркуша X/період Y»),
        а в режимі плану такі аркуші лише переліковано. Reopen виконує людина/координатор, не цей скрипт.
        Фаза 3 (лише з -Execute): PATCH у нову таблицю; з -ClearSource - стирання перенесених/однакових значень
        у джерелі (за замовчуванням джерело не чіпається).
    .OUTPUTS
        pscustomobject із сумарними лічильниками й Pairs (по таблицях) та Blocked (аркуші, що потребують Reopen).
    #>
    param(
        [Parameter(Mandatory)][int[]]$DocumentId,
        [Parameter(Mandatory)][int[]]$PeriodKey,
        [Parameter(Mandatory)]$Maps,
        [switch]$Execute,
        [switch]$OverwriteConflicts,
        [switch]$ClearSource,
        [double]$PaceSeconds = 2.2,
        [int]$ChunkRows = 40,
        [scriptblock]$Log = { param($text) Write-Host $text }
    )

    $res = [pscustomobject]@{
        Documents = 0; Pairs = 0; Values = 0; Moved = 0; Overwritten = 0; Same = 0; Conflicts = 0; ReadOnlyTarget = 0
        UnresolvedColumnValues = 0; UnresolvedRowValues = 0; UnresolvedTables = 0
        RowsWritten = 0; RowsFailed = 0; CellsCleared = 0; ClearFailed = 0
        Executed = [bool]$Execute
        Blocked = (New-Object System.Collections.Generic.List[string])
        PairList = (New-Object System.Collections.Generic.List[object])
    }

    $work = New-Object System.Collections.Generic.List[object]
    foreach ($doc in $DocumentId) {
        $res.Documents++
        foreach ($period in $PeriodKey) {
            $dr = Invoke-LandStaleCall -Method GET -Path "/api/v1/documents/$doc`?periodKey=$period" -PaceSeconds $PaceSeconds
            if ($dr.Status -ne 200) { throw "GET documents/${doc}: HTTP $($dr.Status) $(Get-EcrErrorCode $dr)" }
            $states = Get-LandSheetStateMap $dr.Json
            $tr = Invoke-LandStaleCall -Method GET -Path "/api/v1/documents/$doc/tables?periodKey=$period" -PaceSeconds $PaceSeconds
            if ($tr.Status -eq 404) { continue }
            if ($tr.Status -ne 200) { throw "GET documents/$doc/tables ($period): HTTP $($tr.Status) $(Get-EcrErrorCode $tr)" }
            $inst = @($tr.Json | Where-Object { $null -ne $_ })

            foreach ($m in @($Maps)) {
                $sources = @($inst | Where-Object { [string]$_.tableCode -eq $m.From -and ([string]::IsNullOrEmpty($m.Sheet) -or [string]$_.sheetCode -eq $m.Sheet) })
                foreach ($src in $sources) {
                    $sheet = [string]$src.sheetCode
                    $dst = @($inst | Where-Object { [string]$_.tableCode -eq $m.To -and [string]$_.sheetCode -eq $sheet }) | Select-Object -First 1
                    if ($null -eq $dst) {
                        $any = @($inst | Where-Object { [string]$_.tableCode -eq $m.To })
                        if ($any.Count -eq 1) { $dst = $any[0] }
                    }
                    if ($null -eq $dst) {
                        $res.UnresolvedTables++
                        & $Log "  doc $doc / $period / ${sheet}: таблиці-приймача $($m.To) для $($m.From) немає (нова версія не застосована?) - пропуск."
                        continue
                    }
                    $sg = Invoke-LandStaleCall -Method GET -Path "/api/v1/documents/$doc/tables/$($src.tableInstanceId)?periodKey=$period" -PaceSeconds $PaceSeconds
                    if ($sg.Status -ne 200) { throw "GET зрізу $($m.From) документа ${doc}: HTTP $($sg.Status) $(Get-EcrErrorCode $sg)" }
                    $hasSource = $false
                    foreach ($row in @(Get-LandProp $sg.Json 'rows')) {
                        $co = Get-LandProp $row 'cells'
                        if ($null -ne $co) { foreach ($pp in $co.PSObject.Properties) { if ($null -ne $pp.Value) { $hasSource = $true; break } } }
                        if ($hasSource) { break }
                    }
                    if (-not $hasSource) { continue }
                    $tg = Invoke-LandStaleCall -Method GET -Path "/api/v1/documents/$doc/tables/$($dst.tableInstanceId)?periodKey=$period" -PaceSeconds $PaceSeconds
                    if ($tg.Status -ne 200) { throw "GET зрізу $($m.To) документа ${doc}: HTTP $($tg.Status) $(Get-EcrErrorCode $tg)" }
                    $allowsDyn = $true
                    $ad = Get-LandProp $dst 'allowsDynamicRows'
                    if ($null -ne $ad) { $allowsDyn = [bool]$ad }
                    $plan = Get-LandStaleTablePlan -Source $sg.Json -Target $tg.Json -ColumnMap $m.Columns -AllowsDynamicRows $allowsDyn -OverwriteConflicts ([bool]$OverwriteConflicts)
                    $item = [pscustomobject]@{
                        Document = $doc; Period = $period; Sheet = $sheet; From = $m.From; To = $m.To
                        SourceTi = [long]$src.tableInstanceId; TargetTi = [long]$dst.tableInstanceId
                        State = [string]$states[$sheet]; Plan = $plan
                    }
                    $work.Add($item)
                    $res.Pairs++
                    $res.Values += $plan.Values; $res.Moved += $plan.Moved; $res.Overwritten += $plan.Overwritten; $res.Same += $plan.Same
                    $res.Conflicts += $plan.Conflicts; $res.ReadOnlyTarget += $plan.ReadOnlyTarget
                    $res.UnresolvedColumnValues += $plan.UnresolvedColumnValues; $res.UnresolvedRowValues += $plan.UnresolvedRowValues
                    $cols = ''
                    if ($plan.UnresolvedColumns.Count -gt 0) { $cols = ' [нерозв''язані колонки: ' + ($plan.UnresolvedColumns -join ', ') + ']' }
                    $line = "  doc $doc / $period / $sheet / $($m.From) -> $($m.To): значень $($plan.Values), перенести $($plan.Moved), однакових $($plan.Same), конфліктів $($plan.Conflicts)"
                    $line += ", лише для читання $($plan.ReadOnlyTarget), нерозв'язаних колонок (значень) $($plan.UnresolvedColumnValues), нерозв'язаних рядків (значень) $($plan.UnresolvedRowValues), стан аркуша $($item.State)$cols"
                    & $Log $line
                    $res.PairList.Add([pscustomobject]@{ Document = $doc; Period = $period; Sheet = $sheet; From = $m.From; To = $m.To; Values = $plan.Values; Moved = $plan.Moved; Same = $plan.Same; Conflicts = $plan.Conflicts; State = $item.State })
                }
            }
        }
    }

    # Фаза 2: аркуші, що потребують Reopen (є що писати, а стан Submitted/Approved).
    $blockedKeys = New-Object System.Collections.Generic.HashSet[string]
    foreach ($w in $work) {
        $needsWrite = (($w.Plan.Rows.Count -gt 0) -or ($ClearSource -and $w.Plan.ClearRows.Count -gt 0))
        if ($needsWrite -and (Test-LandSheetLocked $w.State)) {
            $k = "$($w.Sheet)/$($w.Period)"
            if ($blockedKeys.Add($k)) {
                $res.Blocked.Add("потрібен Reopen аркуша $($w.Sheet)/період $($w.Period) — виконує людина/координатор (документ $($w.Document), стан $($w.State))")
            }
        }
    }
    foreach ($blockText in $res.Blocked) { & $Log "  ! $blockText" }
    if ($Execute -and $res.Blocked.Count -gt 0) {
        throw ("Запис не розпочато: " + ($res.Blocked -join '; '))
    }

    # Фаза 3: запис.
    if ($Execute) {
        foreach ($w in $work) {
            $plan = $w.Plan
            $okKeys = New-Object System.Collections.Generic.HashSet[string]
            if ($plan.Rows.Count -gt 0) {
                $s = Send-LandStaleRows -DocumentId $w.Document -PeriodKey $w.Period -TableInstanceId $w.TargetTi -Rows $plan.Rows -ChunkRows $ChunkRows -PaceSeconds $PaceSeconds
                $res.RowsWritten += $s.Done; $res.RowsFailed += $s.Failed
                $okKeys = $s.DoneKeys
                & $Log "  doc $($w.Document) / $($w.Period) / $($w.To): записано рядків $($s.Done), відхилено $($s.Failed)"
            }
            if ($ClearSource) {
                $clearRows = New-Object System.Collections.Generic.List[object]
                $cellCount = 0
                foreach ($cr in $plan.ClearRows) {
                    if ($cr.Patched -and -not $okKeys.Contains($cr.RowKey)) { continue }
                    $cells = @($cr.Columns | ForEach-Object { @{ columnCode = [string]$_; isEmpty = $false; value = $null } })
                    $clearRows.Add(@{ rowKey = $cr.RowKey; baseVersion = $cr.BaseVersion; cells = $cells })
                    $cellCount += $cells.Count
                }
                if ($clearRows.Count -gt 0) {
                    $s2 = Send-LandStaleRows -DocumentId $w.Document -PeriodKey $w.Period -TableInstanceId $w.SourceTi -Rows $clearRows -ChunkRows $ChunkRows -PaceSeconds $PaceSeconds
                    $res.ClearFailed += $s2.Failed
                    if ($s2.Failed -eq 0) { $res.CellsCleared += $cellCount }
                    & $Log "  doc $($w.Document) / $($w.Period) / $($w.From): стерто в джерелі рядків $($s2.Done), відхилено $($s2.Failed)"
                }
            }
        }
    }
    return $res
}
