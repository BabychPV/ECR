# tools/land/LandYearMonthsApply.Lib.ps1
# LAND-YEAR, застосування плану -Stage YearMonths на стенді, де вже опублікована місячна версія LAND_UI (1.0.18.0,
# PerMonthCopies): клон останньої опублікованої -> наступна версія (1.0.19.0) або -YearMonthsTargetVersion, прибрані й
# додані колонки, порядок колонок, псевдоніми місяця (cfg.MonthInputAlias), прив'язки виходів до 12 місячних колонок.
# Чисті функції (план) + тонкі обгортки над Invoke-Ecr (GET, далі запис лише за різниці). Повтор - 0 змін.
# Потребує LandTemplateDraft.Lib.ps1 (Get-LandNextVersionNumber). Сумісна з Windows PowerShell 5.1 і PowerShell 7.

function ConvertTo-LandLayoutName {
    <# Розкладка версії з JSON сервера (рядок або число enum TemplateLayout) -> 'PerMonthCopies' | 'YearWithMonthColumns'. #>
    param($Value)
    if ($null -eq $Value) { return $null }
    $s = [string]$Value
    if ($s -eq '0') { return 'PerMonthCopies' }
    if ($s -eq '1') { return 'YearWithMonthColumns' }
    return $s
}

function Get-LandYearMonthsVersionPlan {
    <#
    .SYNOPSIS
        Чисте рішення, у яку версію будувати річну LAND_UI. Versions - id, version, status; Layouts - id -> розкладка
        (рядок або число) для версій, які треба знати (остання опублікована і, якщо вона є, цільова).
    .OUTPUTS
        Action:
          Clone           - клон останньої опублікованої (SourceId/SourceVersion, розкладка PerMonthCopies) у NewVersion;
          ResumeDraft     - чернетка цільової версії вже є (DraftId): продовжуємо в ній, нового клону немає;
          TargetPublished - цільова версія вже опублікована з YearWithMonthColumns (PublishedId): структуру не чіпати.
        Відмова (throw): немає опублікованої; джерело не PerMonthCopies; ціль опублікована з іншою розкладкою; ціль в
        іншому стані; дві версії з одним номером; новіша за цільову чернетка (Apply-ContractHeader опублікував би її).
    .PARAMETER TargetVersion
        Явна цільова версія (-YearMonthsTargetVersion). Порожньо - остання опублікована, якщо вона вже річна (повтор),
        інакше наступний номер після неї (номери чернеток не займають його: чернетка того номера - продовження).
    #>
    param([object[]]$Versions, [hashtable]$Layouts = @{}, [string]$TargetVersion)
    $all = @($Versions | Where-Object { $null -ne $_ })
    $pubs = @($all | Where-Object { [string]$_.status -eq 'Published' } | Sort-Object { [int]$_.id } -Descending)
    if ($pubs.Count -eq 0) { throw 'YearMonths: у шаблоні немає опублікованої версії - клонувати річну LAND_UI нема з чого. Відмова.' }
    $latest = $pubs[0]
    $latestLayout = ConvertTo-LandLayoutName $Layouts[[int]$latest.id]
    if ([string]::IsNullOrWhiteSpace($latestLayout)) { throw "YearMonths: невідома розкладка опублікованої версії $($latest.version) (id $($latest.id)). Відмова." }
    $target = $TargetVersion
    $why = 'параметр -YearMonthsTargetVersion'
    if ([string]::IsNullOrWhiteSpace($target)) {
        if ($latestLayout -eq 'YearWithMonthColumns') { $target = [string]$latest.version; $why = 'остання опублікована вже річна' }
        else {
            $taken = @($all | Where-Object { [string]$_.status -ne 'Draft' } | ForEach-Object { [string]$_.version })
            $target = Get-LandNextVersionNumber -FromVersion ([string]$latest.version) -Taken $taken
            $why = "наступна після опублікованої $($latest.version)"
        }
    }
    $same = @($all | Where-Object { [string]$_.version -eq $target })
    if ($same.Count -gt 1) { throw "YearMonths: версій з номером $target кілька ($(@($same | ForEach-Object { "id $($_.id) $($_.status)" }) -join ', ')). Відмова." }
    if ($same.Count -eq 1) {
        $t = $same[0]
        switch ([string]$t.status) {
            'Published' {
                $tl = ConvertTo-LandLayoutName $Layouts[[int]$t.id]
                if ($tl -ne 'YearWithMonthColumns') {
                    throw "YearMonths: цільова версія $target уже опублікована (id $($t.id)) з розкладкою $tl, а не YearWithMonthColumns. Відмова: опубліковану версію не змінюють; задайте інший -YearMonthsTargetVersion."
                }
                return [pscustomobject]@{ Action = 'TargetPublished'; PublishedId = [int]$t.id; NewVersion = $target; Why = $why
                    SourceId = [int]$latest.id; SourceVersion = [string]$latest.version }
            }
            'Draft' {
                $newer = @($all | Where-Object { [string]$_.status -eq 'Draft' -and [int]$_.id -gt [int]$t.id })
                if ($newer.Count -gt 0) {
                    throw "YearMonths: після чернетки $target (id $($t.id)) є новіша чернетка $($newer[0].version) (id $($newer[0].id)); публікація шапкою (Apply-ContractHeader) взяла б саме її. Відмова."
                }
                return [pscustomobject]@{ Action = 'ResumeDraft'; DraftId = [int]$t.id; DraftVersion = $target; NewVersion = $target; Why = $why
                    SourceId = [int]$latest.id; SourceVersion = [string]$latest.version }
            }
            default { throw "YearMonths: цільова версія $target (id $($t.id)) у стані $($t.status) - ні продовжити, ні клонувати в неї не можна. Відмова." }
        }
    }
    if ($latestLayout -ne 'PerMonthCopies') {
        throw "YearMonths: джерело клону - остання опублікована $($latest.version) (id $($latest.id)) - має розкладку $latestLayout, а не PerMonthCopies; план YearMonths будується лише з місячної версії. Відмова."
    }
    return [pscustomobject]@{ Action = 'Clone'; SourceId = [int]$latest.id; SourceVersion = [string]$latest.version; NewVersion = $target; Why = $why
        SkippedDrafts = @($all | Where-Object { [string]$_.status -eq 'Draft' } | ForEach-Object { "$($_.version) (id $($_.id))" }) }
}

function Get-LandMonthInputAliasItems {
    <# Псевдоніми плану (Table, Alias, Month, Column) -> тіло PUT …/month-input-aliases (sheetCode, tableCode, alias, monthNumber, columnCode). #>
    param([Parameter(Mandatory)]$Model, [object[]]$Aliases)
    $sheetOf = @{}
    foreach ($sh in @($Model.sheets)) { foreach ($tb in @($sh.tables)) { $sheetOf[[string]$tb.code] = [string]$sh.code } }
    $rows = @(foreach ($a in @($Aliases | Where-Object { $_ })) {
            $t = [string]$a.Table
            if (-not $sheetOf.ContainsKey($t)) { throw "Псевдонім $($a.Alias)/$($a.Month): таблиці $t немає в моделі." }
            [pscustomobject][ordered]@{ sheetCode = $sheetOf[$t]; tableCode = $t; alias = [string]$a.Alias; monthNumber = [int]$a.Month; columnCode = [string]$a.Column }
        })
    return @($rows | Sort-Object sheetCode, tableCode, alias, monthNumber)
}

function Get-LandMonthInputAliasKey {
    param($Item)
    return ('{0}|{1}|{2}|{3}|{4}' -f [string]$Item.sheetCode, [string]$Item.tableCode, [string]$Item.alias, [int]$Item.monthNumber, [string]$Item.columnCode)
}

function Compare-LandMonthInputAliases {
    <# Набір проти наявного: Same (повтор - PUT не потрібен), Add, Remove (ключі sheet|table|alias|month|column). #>
    param([object[]]$Want, [object[]]$Have)
    $w = @{}; foreach ($x in @($Want | Where-Object { $_ })) { $w[(Get-LandMonthInputAliasKey $x)] = $true }
    $h = @{}; foreach ($x in @($Have | Where-Object { $_ })) { $h[(Get-LandMonthInputAliasKey $x)] = $true }
    $add = @($w.Keys | Where-Object { -not $h.ContainsKey($_) } | Sort-Object)
    $rem = @($h.Keys | Where-Object { -not $w.ContainsKey($_) } | Sort-Object)
    return [pscustomobject]@{ Same = ($add.Count -eq 0 -and $rem.Count -eq 0); Add = $add; Remove = $rem; Want = $w.Count; Have = $h.Count }
}

function Invoke-LandMonthInputAliases {
    <#
    .SYNOPSIS
        GET …/month-input-aliases чернетки; PUT повного набору лише за різниці (сервер - replace-all, лише чернетка).
        Повертає @{ Want; Have; Add; Remove; Put }.
    #>
    param([Parameter(Mandatory)][int]$VersionId, [object[]]$Items, [scriptblock]$Log = { param($m) Write-Host $m }, [switch]$DryRun, [int]$First = 5)
    $path = "/api/v1/template-versions/$VersionId/month-input-aliases"
    $g = Invoke-Ecr -Method GET -Path $path
    if ($g.Status -in 404, 405 -and -not $g.Json.errorCode) { throw "GET $path`: HTTP $($g.Status) - сервер без L0b (month-input-aliases); псевдоніми місяця поставити неможливо." }
    if ($g.Status -ne 200) { throw "GET $path`: HTTP $($g.Status) $(Get-EcrErrorCode $g) $($g.Text)" }
    $current = @($g.Json.items)
    $cmp = Compare-LandMonthInputAliases -Want $Items -Have $current
    & $Log ("  псевдоніми місяця (L0): у плані {0}, у чернетці {1}; додати {2}, прибрати {3}" -f $cmp.Want, $cmp.Have, @($cmp.Add).Count, @($cmp.Remove).Count)
    foreach ($k in @($cmp.Add | Select-Object -First $First)) { & $Log "    + $k" }
    foreach ($k in @($cmp.Remove | Select-Object -First $First)) { & $Log "    - $k" }
    $res = [pscustomobject]@{ Want = $cmp.Want; Have = $cmp.Have; Add = @($cmp.Add).Count; Remove = @($cmp.Remove).Count; Put = $false }
    if ($cmp.Same) { & $Log '  псевдоніми місяця: без змін (PUT не потрібен)'; return $res }
    if ($DryRun) { & $Log "  DryRun: PUT $path (повний набір $($cmp.Want)) - пропущено"; return $res }
    $r = Invoke-Ecr -Method PUT -Path $path -Body @{ items = @($Items) }
    if ($r.Status -ne 200) { throw "PUT $path`: HTTP $($r.Status) $(Get-EcrErrorCode $r) $(([string]$r.Text).Substring(0, [Math]::Min(1500, ([string]$r.Text).Length)))" }
    $back = @($r.Json.items).Count
    if ($back -ne $cmp.Want) { throw "PUT $path`: сервер повернув $back псевдонімів, а в плані $($cmp.Want)." }
    $res.Put = $true
    & $Log "  псевдоніми місяця записано: $back (PUT replace-all)"
    return $res
}

function Get-LandYearMonthsRemovals {
    <#
    .SYNOPSIS
        Живі колонки чернетки, які план YearMonths прибирає (table.yearMonths.removed: селектори, змінні формули й
        немісячні результати). Live - @{ Sheet; Table; Code; Id }. Повертає ті самі об'єкти; повтор (уже прибрані) - порожньо.
    #>
    param([Parameter(Mandatory)]$Model, [object[]]$Live)
    $rm = @{}
    foreach ($sh in @($Model.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $tym = $tb.PSObject.Properties['yearMonths']
            if (-not $tym) { continue }
            foreach ($c in @($tym.Value.removed)) { $rm["$($sh.code)/$($tb.code)/$c"] = $true }
        }
    }
    return @($Live | Where-Object { $_ -and $rm.ContainsKey("$($_.Sheet)/$($_.Table)/$($_.Code)") })
}

function Get-LandYearMonthsOrdinalPlan {
    <#
    .SYNOPSIS
        Порядок живих колонок таблиць із планом YearMonths = порядок моделі (1..n); живі колонки поза моделлю - після
        них у поточному порядку. Без цього нові _Mnn (Ordinal = позиція в моделі) змішувалися б з успадкованими
        Ordinal клону і групи виходів MB_O_* були б несуміжні. Live - @{ Sheet; Table; Code; Id; Ordinal }.
        Повертає @(@{ Id; Key; From; To }) лише для різниць.
    #>
    param([Parameter(Mandatory)]$Model, [object[]]$Live)
    $byTable = @{}
    foreach ($c in @($Live | Where-Object { $_ })) {
        $k = "$($c.Sheet)/$($c.Table)"
        if (-not $byTable.ContainsKey($k)) { $byTable[$k] = New-Object 'System.Collections.Generic.List[object]' }
        $byTable[$k].Add($c)
    }
    $out = New-Object 'System.Collections.Generic.List[object]'
    foreach ($sh in @($Model.sheets)) {
        foreach ($tb in @($sh.tables)) {
            if (-not $tb.PSObject.Properties['yearMonths']) { continue }
            $k = "$($sh.code)/$($tb.code)"
            if (-not $byTable.ContainsKey($k)) { continue }
            $byCode = @{}; foreach ($c in $byTable[$k]) { $byCode[[string]$c.Code] = $c }
            $o = 0; $done = @{}
            foreach ($mc in @($tb.columns)) {
                $o++
                $c = $byCode[[string]$mc.code]
                if ($null -eq $c) { continue }
                $done[[string]$mc.code] = $true
                if ([int]$c.Ordinal -ne $o) { $out.Add([pscustomobject]@{ Id = [int]$c.Id; Key = "$k/$($mc.code)"; From = [int]$c.Ordinal; To = $o }) }
            }
            foreach ($c in @($byTable[$k] | Where-Object { -not $done.ContainsKey([string]$_.Code) } | Sort-Object { [int]$_.Ordinal }, { [string]$_.Code })) {
                $o++
                if ([int]$c.Ordinal -ne $o) { $out.Add([pscustomobject]@{ Id = [int]$c.Id; Key = "$k/$($c.Code)"; From = [int]$c.Ordinal; To = $o }) }
            }
        }
    }
    return $out.ToArray()
}

function Get-LandBindingPlan {
    <#
    .SYNOPSIS
        Прив'язки методології: бажані (ColumnDefId, OutputCode, IsActive; предикат '{}') проти наявних з GET
        /methodologies/{id}/bindings. Повертає Put (створити або змінити) і Same (повтор - нічого).
    #>
    param([object[]]$Wanted, [object[]]$Existing)
    $byKey = @{}
    foreach ($b in @($Existing | Where-Object { $_ })) { $byKey["$([int]$b.columnDefId)|$([string]$b.outputCode)"] = $b }
    $put = New-Object 'System.Collections.Generic.List[object]'; $same = 0
    foreach ($w in @($Wanted | Where-Object { $_ })) {
        $e = $byKey["$([int]$w.ColumnDefId)|$([string]$w.OutputCode)"]
        if ($null -ne $e -and [bool]$e.isActive -eq [bool]$w.IsActive -and [string]$e.matchJson -eq '{}') { $same++; continue }
        $put.Add([pscustomobject]@{ ColumnDefId = [int]$w.ColumnDefId; OutputCode = [string]$w.OutputCode; IsActive = [bool]$w.IsActive; Create = ($null -eq $e); Label = [string]$w.Label })
    }
    return [pscustomobject]@{ Put = $put.ToArray(); Same = $same }
}

function Set-LandMethodologyBindings {
    <#
    .SYNOPSIS
        Ідемпотентні прив'язки однієї методології: ОДИН GET наявних, далі PUT лише відсутніх/змінених.
        Повертає @{ Created; Updated; Same; Failed; Errors }.
    #>
    param([Parameter(Mandatory)][int]$MethodologyId, [object[]]$Wanted, [switch]$DryRun, [scriptblock]$Log = { param($m) Write-Host $m })
    $res = [pscustomobject]@{ Created = 0; Updated = 0; Same = 0; Failed = 0; Errors = @() }
    if (@($Wanted).Count -eq 0) { return $res }
    $g = Invoke-Ecr -Method GET -Path "/api/v1/methodologies/$MethodologyId/bindings"
    if ($g.Status -ne 200) { throw "GET /methodologies/$MethodologyId/bindings: HTTP $($g.Status) $(Get-EcrErrorCode $g)" }
    $plan = Get-LandBindingPlan -Wanted $Wanted -Existing @($g.Json)
    $res.Same = $plan.Same
    foreach ($p in @($plan.Put)) {
        if ($DryRun) { if ($p.Create) { $res.Created++ } else { $res.Updated++ }; continue }
        $r = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$MethodologyId/bindings/$($p.ColumnDefId)/$($p.OutputCode)" -Body @{ isActive = $p.IsActive; matchJson = '{}' }
        if ($r.Status -eq 200) { if ($p.Create) { $res.Created++ } else { $res.Updated++ } }
        else { $res.Failed++; $res.Errors += "$($p.Label)/$($p.OutputCode) (колонка $($p.ColumnDefId)): HTTP $($r.Status) $(Get-EcrErrorCode $r)" }
    }
    return $res
}

function Write-LandYearMonthsPlanReport {
    <#
    .SYNOPSIS
        -DryRun: план застосування YearMonths по кроках (кількість + перші N) з моделі після перебудови, без мережі.
        Різницю проти конкретної чернетки друкує звичайний прогін (кожен крок пише «додано/без змін»).
    #>
    param([Parameter(Mandatory)]$Ym, [string]$TargetVersion, [int]$First = 5, [scriptblock]$Log = { param($m) Write-Host $m })
    $added = New-Object 'System.Collections.Generic.List[string]'; $removed = New-Object 'System.Collections.Generic.List[string]'
    $totals = New-Object 'System.Collections.Generic.List[string]'; $varying = New-Object 'System.Collections.Generic.List[string]'
    foreach ($sh in @($Ym.Model.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $tym = $tb.PSObject.Properties['yearMonths']
            if (-not $tym) { continue }
            foreach ($c in @($tb.columns)) {
                if ($c.PSObject.Properties['yearMonthsOf']) {
                    $added.Add("$($tb.code).$($c.code)")
                    if ($c.expression) { $varying.Add("$($tb.code).$($c.code)") }
                }
            }
            foreach ($x in @($tym.Value.removed)) { $removed.Add("$($tb.code).$x") }
            foreach ($x in @($tym.Value.totals)) { $totals.Add("$($tb.code).$x") }
        }
    }
    $show = { param($title, $list, $verb)
        $arr = @($list)
        & $Log ("  [{0}] {1}: {2}" -f $title, $verb, $arr.Count)
        foreach ($i in @($arr | Select-Object -First $First)) { & $Log "      $i" }
        if ($arr.Count -gt $First) { & $Log "      ... ще $($arr.Count - $First)" }
    }
    $tv = if ($TargetVersion) { $TargetVersion } else { 'наступна після останньої опублікованої (PerMonthCopies)' }
    & $Log "DryRun YearMonths: план застосування (з моделі; стенд не читається)"
    & $Log "  [1 версія] клон останньої опублікованої PerMonthCopies -> $tv; наявна чернетка цієї версії - продовження; опублікована з іншою розкладкою - відмова"
    & $show '2 колонки +' $added 'додати (місячні _Mnn)'
    & $show '2 колонки +' $totals 'додати (річні підсумки xlsm)'
    & $show '2 формули' $varying 'формул колонок для нових змінних _Mnn'
    & $show '3 колонки -' $removed "прибрати (м'яко, SQL; разом з їхніми формулами)"
    & $Log ("  [4 порядок] Ordinal живих колонок {0} таблиць = порядок моделі (лише різниці)" -f @($Ym.Tables).Count)
    & $show '5 групи виходів' @($Ym.Groups | ForEach-Object { "$($_.Table).$($_.Code) ($(@($_.Columns).Count))" }) 'MB_O_* груп (PUT header-groups, лише різниця)'
    & $Log '  [6 розкладка] PUT layout = YearWithMonthColumns (лише чернетка)'
    & $show '7 псевдоніми' @($Ym.Aliases | ForEach-Object { "$($_.Table).$($_.Alias)/M$('{0:00}' -f [int]$_.Month) -> $($_.Column)" }) 'псевдонімів місяця (PUT лише за різниці)'
    & $Log '  [8 публікація] Apply-ContractHeader -UseExistingDraft (шапка + publish)'
    & $show '9 прив''язки' @($Ym.Bindings | ForEach-Object { "$($_.Table).$($_.Output) -> $($_.Column)" }) "прив'язок виходів до місячних колонок (-Stage Methodologies, PUT лише відсутніх)"
}
