<#
    Pester (3/5 сумісний синтаксис без `Should`): B5 17.11 - -Stage YearMonths застосовує план на стенді з опублікованою
    місячною LAND_UI 1.0.18.0 (tools/land/LandYearMonthsApply.Lib.ps1 + Build-LandTemplate.ps1). Мережі немає: Invoke-Ecr
    підмінено й журналює виклики.
#>

Describe 'Build-LandTemplate -Stage YearMonths: застосування плану (B5)' {
    BeforeAll {
    $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
    $land = Join-Path (Join-Path $repo 'tools') 'land'
    . (Join-Path $land 'EcrApi.ps1')
    . (Join-Path $land 'LandTemplateDraft.Lib.ps1')
    . (Join-Path $land 'LandMonthBands.Lib.ps1')
    . (Join-Path $land 'LandYearMonths.Lib.ps1')
    . (Join-Path $land 'LandYearMonthsApply.Lib.ps1')
    Set-StrictMode -Off
    function New-YmFixture {
        $col = { param($Code, $Type, $Month, $Expr) $h = [ordered]@{ code = $Code; dataType = $Type; month = $Month; headerEn = "H $Code"; headerRu = ''; hidden = $false; readOnly = $false; excel = 'Z' }; if ($Expr) { $h.expression = $Expr }; $h }
        $a = @((& $col 'Fuel' 'Lookup' $null $null))
        foreach ($m in 1..12) { $a += & $col ('X_M{0:00}' -f $m) 'Decimal' $m $null }
        $a += & $col 'Sel' 'Formula' $null (((1..11 | ForEach-Object { '[Period].Sequence = {0} ? [X_M{0:00}] : ' -f $_ }) -join '') + '[X_M12]')
        $a += & $col 'Out_A' 'Calculated' $null $null
        $model = @{ versionNumber = '1.0.4.0'; conditionalFormats = @(); sheets = @(
                @{ code = 'S1'; tables = @(@{ code = 'A'; layout = 'MonthsInColumns'; columns = $a; rules = @() }) }) }
        return ($model | ConvertTo-Json -Depth 20) | ConvertFrom-Json
    }
    function V($id, $ver, $st) { [pscustomobject]@{ id = $id; version = $ver; status = $st } }
    }

    It 'гейт «лише план» знято: YearMonths разом з Template працює; лишились -Layout YearWithMonthColumns і -StructureMode Sql' {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $land 'Build-LandTemplate.ps1'), [ref]$null, [ref]$null)
        $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Stage-YearMonths' }, $true)
        if (-not $fn) { throw 'Stage-YearMonths не знайдено' }
        $here = $land; $YearMonthsPlanOut = $null
        function Log([string]$m) { }
        # $script: у тексті етапу -> $global: (області Pester 3/5 різні); прибирається в кінці тесту
        . ([scriptblock]::Create($fn.Extent.Text.Replace('$script:', '$global:')))
        $try = { try { Stage-YearMonths; 'OK' } catch { $_.Exception.Message } }
        $global:Mdl = New-YmFixture; $global:YearMonthsPlan = $null
        $Stage = @('YearMonths', 'Template', 'Methodologies'); $DryRun = $false; $Layout = 'YearWithMonthColumns'; $StructureMode = 'Sql'
        $r = & $try
        if ($r -ne 'OK') { throw "з Template/Methodologies без -DryRun мусить працювати: $r" }
        if (-not $global:YearMonthsPlan -or @($global:YearMonthsPlan.Aliases).Count -ne 12 -or @($global:YearMonthsPlan.Bindings).Count -ne 12) { throw 'план не збережено для етапів Template/Methodologies' }
        $global:Mdl = New-YmFixture; $StructureMode = 'Api'
        if ((& $try) -notlike '*-StructureMode Sql*') { throw 'Template без Sql мусить відмовити' }
        $global:Mdl = New-YmFixture; $StructureMode = 'Sql'; $Layout = 'PerMonthCopies'
        if ((& $try) -notlike '*потребує -Layout YearWithMonthColumns*') { throw 'без річної розкладки мусить відмовити' }
        $txt = [IO.File]::ReadAllText((Join-Path $land 'Build-LandTemplate.ps1'))
        Remove-Variable -Name Mdl, YearMonthsPlan -Scope Global -ErrorAction SilentlyContinue
        if ($txt -like '*поки заборонено*' -or $txt -like '*YearMonths будує версію*') { throw 'залишки старого гейта / перевірки 1.0.17.0' }
    }

    It 'вибір версії: 1.0.18 (PerMonthCopies) -> клон 1.0.19; -YearMonthsTargetVersion; чернетки інших номерів не займають ціль' {
        $vers = @((V 10 '1.0.17.0' 'Published'), (V 11 '1.0.18.0' 'Published'))
        $p = Get-LandYearMonthsVersionPlan -Versions $vers -Layouts @{ 11 = 'PerMonthCopies' }
        if ($p.Action -ne 'Clone' -or $p.SourceId -ne 11 -or $p.NewVersion -ne '1.0.19.0') { throw "план: $($p.Action) $($p.SourceId) $($p.NewVersion)" }
        $p = Get-LandYearMonthsVersionPlan -Versions $vers -Layouts @{ 11 = 0 } -TargetVersion '1.0.25.0'
        if ($p.Action -ne 'Clone' -or $p.NewVersion -ne '1.0.25.0') { throw "явна ціль: $($p.Action) $($p.NewVersion)" }
        $p = Get-LandYearMonthsVersionPlan -Versions ($vers + @(V 12 '1.1.1.9999' 'Draft')) -Layouts @{ 11 = 'PerMonthCopies' }
        if ($p.Action -ne 'Clone' -or $p.NewVersion -ne '1.0.19.0' -or @($p.SkippedDrafts).Count -ne 1) { throw "чужа чернетка: $($p.Action) $($p.NewVersion)" }
    }

    It 'вибір версії: відмови - джерело не PerMonthCopies, ціль опублікована з іншою розкладкою, новіша чернетка, немає опублікованої' {
        $msg = { param($sb) try { & $sb; 'NO THROW' } catch { $_.Exception.Message } }
        $vers = @((V 11 '1.0.18.0' 'Published'))
        $m1 = & $msg { Get-LandYearMonthsVersionPlan -Versions $vers -Layouts @{ 11 = 'YearWithMonthColumns' } -TargetVersion '1.0.20.0' }
        if ($m1 -notlike '*не PerMonthCopies*') { throw "джерело річне: $m1" }
        $m2 = & $msg { Get-LandYearMonthsVersionPlan -Versions @((V 11 '1.0.18.0' 'Published'), (V 12 '1.0.19.0' 'Published')) -Layouts @{ 12 = 'PerMonthCopies' } -TargetVersion '1.0.19.0' }
        if ($m2 -notlike '*уже опублікована*PerMonthCopies*') { throw "ціль опублікована місячною: $m2" }
        $m3 = & $msg { Get-LandYearMonthsVersionPlan -Versions @((V 11 '1.0.18.0' 'Published'), (V 13 '1.0.19.0' 'Draft'), (V 14 '1.1.0.0' 'Draft')) -Layouts @{ 11 = 'PerMonthCopies' } }
        if ($m3 -notlike '*новіша чернетка*') { throw "новіша чернетка: $m3" }
        $m4 = & $msg { Get-LandYearMonthsVersionPlan -Versions @((V 13 '1.0.19.0' 'Draft')) -Layouts @{} }
        if ($m4 -notlike '*немає опублікованої*') { throw "без опублікованої: $m4" }
        # повтор: остання опублікована вже річна - без змін
        $p = Get-LandYearMonthsVersionPlan -Versions @((V 11 '1.0.18.0' 'Published'), (V 12 '1.0.19.0' 'Published')) -Layouts @{ 12 = 1 }
        if ($p.Action -ne 'TargetPublished' -or $p.PublishedId -ne 12) { throw "повтор: $($p.Action)" }
    }

    It 'продовження: наявна чернетка 1.0.19 береться, клону (POST) немає; свіжий випадок - один POST clone 1.0.18 -> 1.0.19' {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $land 'Build-LandTemplate.ps1'), [ref]$null, [ref]$null)
        $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Select-LandYearMonthsDraft' }, $true)
        if (-not $fn) { throw 'Select-LandYearMonthsDraft не знайдено' }
        . ([scriptblock]::Create($fn.Extent.Text))
        $script:Calls = New-Object System.Collections.ArrayList
        function Log([string]$m) { }
        function Invoke-Ecr {
            param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{})
            [void]$script:Calls.Add("$Method $Path")
            if ($Path -like '*/structure') { return [pscustomobject]@{ Status = 200; Json = [pscustomobject]@{ layout = 'PerMonthCopies' }; Text = ''; Headers = @{} } }
            if ($Method -eq 'POST') { return [pscustomobject]@{ Status = 201; Json = [pscustomobject]@{ versionId = 99 }; Text = ''; Headers = @{} } }
            throw "неочікуваний виклик $Method $Path"
        }
        function Api { param([string]$Method, [string]$Path, $Payload = $null) return (Invoke-Ecr -Method $Method -Path $Path -Body $Payload) }
        $S = @{}; $YearMonthsTargetVersion = $null
        $id = Select-LandYearMonthsDraft @((V 11 '1.0.18.0' 'Published'), (V 13 '1.0.19.0' 'Draft'))
        if ($id -ne 13) { throw "продовження мусить взяти чернетку 13, а дало $id" }
        if (@($script:Calls | Where-Object { $_ -like 'POST*' }).Count -ne 0) { throw "продовження без клону: $($script:Calls -join ';')" }
        $script:Calls.Clear()
        $id = Select-LandYearMonthsDraft @((V 11 '1.0.18.0' 'Published'))
        if ($id -ne 99 -or (@($script:Calls | Where-Object { $_ -like 'POST*' }) -join ';') -ne 'POST /api/v1/template-versions/11/clone') { throw "клон: $id $($script:Calls -join ';')" }
    }

    It 'псевдоніми місяця: GET спершу; PUT лише за різниці (повний набір); -DryRun не пише' {
        $mdl = New-YmFixture
        $al = @(1..12 | ForEach-Object { [pscustomobject]@{ Table = 'A'; Alias = 'Sel'; Month = $_; Column = ('X_M{0:00}' -f $_) } })
        $items = Get-LandMonthInputAliasItems -Model $mdl -Aliases $al
        if (@($items).Count -ne 12 -or $items[0].sheetCode -ne 'S1' -or $items[11].columnCode -ne 'X_M12') { throw 'тіло PUT' }
        $script:Calls = New-Object System.Collections.ArrayList
        $script:Have = @()
        function Invoke-Ecr {
            param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{})
            [void]$script:Calls.Add("$Method $Path $(if ($Body) { @($Body.items).Count })")
            if ($Method -eq 'GET') { return [pscustomobject]@{ Status = 200; Json = [pscustomobject]@{ templateVersionId = 7; items = $script:Have }; Text = ''; Headers = @{} } }
            return [pscustomobject]@{ Status = 200; Json = [pscustomobject]@{ templateVersionId = 7; items = @($Body.items) }; Text = ''; Headers = @{} }
        }
        $q = { param($m) }
        $r = Invoke-LandMonthInputAliases -VersionId 7 -Items $items -Log $q
        if (-not $r.Put -or ($script:Calls -join ';') -ne 'GET /api/v1/template-versions/7/month-input-aliases ;PUT /api/v1/template-versions/7/month-input-aliases 12') { throw "різниця: $($script:Calls -join ';')" }
        $script:Calls.Clear(); $script:Have = @($items)
        $r = Invoke-LandMonthInputAliases -VersionId 7 -Items $items -Log $q
        if ($r.Put -or @($script:Calls).Count -ne 1) { throw "повтор - лише GET: $($script:Calls -join ';')" }
        $script:Calls.Clear(); $script:Have = @($items | Select-Object -First 11)
        $r = Invoke-LandMonthInputAliases -VersionId 7 -Items $items -Log $q -DryRun
        if ($r.Put -or $r.Add -ne 1 -or @($script:Calls | Where-Object { $_ -like 'PUT*' }).Count -ne 0) { throw "DryRun пише: $($script:Calls -join ';')" }
    }

    It "прив'язки: один GET на методологію, PUT лише відсутніх/змінених; -DryRun - нуль PUT" {
        $script:Calls = New-Object System.Collections.ArrayList
        function Invoke-Ecr {
            param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{})
            [void]$script:Calls.Add("$Method $Path")
            if ($Method -eq 'GET') { return [pscustomobject]@{ Status = 200; Json = @([pscustomobject]@{ columnDefId = 1; outputCode = 'O'; isActive = $true; matchJson = '{}' }, [pscustomobject]@{ columnDefId = 2; outputCode = 'O'; isActive = $false; matchJson = '{}' }); Text = ''; Headers = @{} } }
            return [pscustomobject]@{ Status = 200; Json = $null; Text = ''; Headers = @{} }
        }
        $want = @(1, 2, 3 | ForEach-Object { [pscustomobject]@{ ColumnDefId = $_; OutputCode = 'O'; IsActive = $true; Label = 'M' } })
        $r = Set-LandMethodologyBindings -MethodologyId 5 -Wanted $want
        if ($r.Same -ne 1 -or $r.Updated -ne 1 -or $r.Created -ne 1 -or ($script:Calls -join ';') -ne 'GET /api/v1/methodologies/5/bindings;PUT /api/v1/methodologies/5/bindings/2/O;PUT /api/v1/methodologies/5/bindings/3/O') { throw "прив'язки: $($script:Calls -join ';')" }
        $script:Calls.Clear()
        $r = Set-LandMethodologyBindings -MethodologyId 5 -Wanted $want -DryRun
        if ($r.Created -ne 1 -or @($script:Calls | Where-Object { $_ -like 'PUT*' }).Count -ne 0) { throw "DryRun пише: $($script:Calls -join ';')" }
    }

    It "успадковані прив'язки (AU L8): прибрана базова колонка -> 12 місячних тієї ж методології й виходу; точний код; неповні/без місяців у звіт" {
        $rows = @()
        foreach ($m in 1..12) { $rows += [pscustomobject]@{ MethodologyId = 9; OutputCode = 'O_1'; TableCode = 'T5_08'; BaseCode = 'O_1'; MonthColumnId = 100 + $m; MonthCode = ('O_1_M{0:00}' -f $m); MonthNumber = $m } }
        # LIKE-хибнопозитив: '_' у коді в SQL - шаблон; функція мусить відкинути
        $rows += [pscustomobject]@{ MethodologyId = 9; OutputCode = 'O_1'; TableCode = 'T5_08'; BaseCode = 'O_1'; MonthColumnId = 999; MonthCode = 'OX1_M01'; MonthNumber = 1 }
        foreach ($m in 1..3) { $rows += [pscustomobject]@{ MethodologyId = 11; OutputCode = 'P'; TableCode = 'T5_22'; BaseCode = 'P'; MonthColumnId = 200 + $m; MonthCode = ('P_M{0:00}' -f $m); MonthNumber = $m } }
        $rows += [pscustomobject]@{ MethodologyId = 11; OutputCode = 'Q'; TableCode = 'T5_22'; BaseCode = 'Q'; MonthColumnId = [DBNull]::Value; MonthCode = [DBNull]::Value; MonthNumber = [DBNull]::Value }
        $co = Get-LandCarryOverMonthBindings -BindingRows $rows
        $w9 = @($co.Wanted[9]); $w11 = @($co.Wanted[11])
        if ($w9.Count -ne 12 -or (@($w9 | ForEach-Object { $_.ColumnDefId }) -join ',') -ne ((101..112) -join ',') -or @($w9 | Where-Object { $_.OutputCode -ne 'O_1' -or -not $_.IsActive }).Count) { throw "методологія 9: $(@($w9 | ForEach-Object { "$($_.ColumnDefId)/$($_.OutputCode)" }) -join ',')" }
        if ($w11.Count -ne 3 -or @($co.Incomplete).Count -ne 1 -or @($co.Incomplete)[0] -notlike 'T5_22/P/P*3') { throw "неповні: $(@($co.Incomplete) -join ';') / $($w11.Count)" }
        if (@($co.NoMonths).Count -ne 1 -or @($co.NoMonths)[0] -notlike 'T5_22/Q/Q*') { throw "без місяців: $(@($co.NoMonths) -join ';')" }
        if ($co.Wanted.Count -ne 2) { throw "методологій: $($co.Wanted.Count)" }
    }

    It 'колонки клону: прибрати лише живі коди плану; порядок = модель, лише різниці; повтор - порожньо' {
        $mdl = New-YmFixture
        $months = (Read-LandMonthBandSource (Join-Path $land 'land-month-bands.json')).months
        $ym = Convert-LandYearMonthsModel -Model $mdl -MonthNames $months
        $live = @([pscustomobject]@{ Sheet = 'S1'; Table = 'A'; Code = 'Fuel'; Id = 1; Ordinal = 1 })
        foreach ($m in 1..12) { $live += [pscustomobject]@{ Sheet = 'S1'; Table = 'A'; Code = ('X_M{0:00}' -f $m); Id = 1 + $m; Ordinal = 1 + $m } }
        $live += [pscustomobject]@{ Sheet = 'S1'; Table = 'A'; Code = 'Sel'; Id = 14; Ordinal = 14 }
        $live += [pscustomobject]@{ Sheet = 'S1'; Table = 'A'; Code = 'Out_A'; Id = 15; Ordinal = 15 }
        foreach ($m in 1..12) { $live += [pscustomobject]@{ Sheet = 'S1'; Table = 'A'; Code = ('Out_A_M{0:00}' -f $m); Id = 100 + $m; Ordinal = 13 + $m } }
        $gone = @(Get-LandYearMonthsRemovals -Model $ym.Model -Live $live)
        if ((@($gone | ForEach-Object { $_.Code }) -join ',') -ne 'Sel,Out_A') { throw "прибрати: $(@($gone | ForEach-Object { $_.Code }) -join ',')" }
        $rest = @($live | Where-Object { $_.Code -notin 'Sel', 'Out_A' })
        $ord = @(Get-LandYearMonthsOrdinalPlan -Model $ym.Model -Live $rest)
        if ($ord.Count -ne 0) { throw "порядок уже за моделлю, а план змін $($ord.Count): $(@($ord | ForEach-Object { "$($_.Key) $($_.From)->$($_.To)" }) -join ',')" }
        $rest[13].Ordinal = 3
        $ord = @(Get-LandYearMonthsOrdinalPlan -Model $ym.Model -Live $rest)
        if ($ord.Count -ne 1 -or $ord[0].Id -ne 101 -or $ord[0].To -ne 14) { throw "різниця порядку: $(@($ord | ForEach-Object { "$($_.Id):$($_.To)" }) -join ',')" }
        if (@(Get-LandYearMonthsRemovals -Model $ym.Model -Live $rest).Count -ne 0) { throw 'повтор прибирання мусить бути порожнім' }
    }

    It '-DryRun: план по кроках без мережі (жодного виклику API)' {
        $mdl = New-YmFixture
        $months = (Read-LandMonthBandSource (Join-Path $land 'land-month-bands.json')).months
        $ym = Convert-LandYearMonthsModel -Model $mdl -MonthNames $months
        $script:Calls = New-Object System.Collections.ArrayList
        function Invoke-Ecr { param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{}) [void]$script:Calls.Add("$Method $Path"); throw 'мережа в DryRun' }
        $script:Out = New-Object System.Collections.ArrayList
        Write-LandYearMonthsPlanReport -Ym $ym -TargetVersion '1.0.19.0' -First 2 -Log { param($m) [void]$script:Out.Add($m) }
        $txt = $script:Out -join "`n"
        if ($script:Calls.Count -ne 0) { throw "виклики: $($script:Calls -join ';')" }
        foreach ($need in '-> 1.0.19.0', 'додати (місячні _Mnn): 12', 'прибрати', ': 2', 'псевдонімів місяця (PUT лише за різниці): 12', "прив'язок виходів до місячних колонок", 'A.Out_A -> Out_A_M01', '... ще 10') {
            if ($txt -notlike "*$need*") { throw "у звіті немає «$need»:`n$txt" }
        }
        $build = [IO.File]::ReadAllText((Join-Path $land 'Build-LandTemplate.ps1'))
        if ($build -notmatch 'if \(\$DryRun\) \{\s+if \(\$script:YearMonthsPlan\) \{ Write-LandYearMonthsPlanReport') { throw 'Build -DryRun не друкує план YearMonths до виходу без API' }
    }
}
