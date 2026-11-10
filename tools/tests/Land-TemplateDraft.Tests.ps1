<#
    Pester (3/5 сумісний синтаксис без `Should`): tools/land/LandTemplateDraft.Lib.ps1 — етап Template будує зі СВІЖОГО
    клону останньої опублікованої версії, наявну чернетку не читає й не пише (репетиція §4' на EcrLandMB: стара чернетка
    1.1.1.9999 принесла чужу групу G1 і MonthNumber T3_00.Land_NAE). -ReuseDraft — стара поведінка. Мережі немає.
#>

Describe 'Build-LandTemplate: свіжий клон замість наявної чернетки' {
    It 'план: є чернетка 1.1.1.9999 (id 7) -> клон опублікованої 1.0.14.0 у 1.0.15.0, чернетку пропущено; зайнятий номер обходиться' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/LandTemplateDraft.Lib.ps1')
        $vers = @(
            [pscustomobject]@{ id = 4; version = '1.0.13.0'; status = 'Published' }
            [pscustomobject]@{ id = 5; version = '1.0.14.0'; status = 'Published' }
            [pscustomobject]@{ id = 7; version = '1.1.1.9999'; status = 'Draft' }
        )
        $p = Get-LandTemplateDraftPlan -Versions $vers
        if ($p.Action -ne 'Clone' -or $p.SourceId -ne 5 -or $p.NewVersion -ne '1.0.15.0') { throw "план: $($p.Action) $($p.SourceId) $($p.NewVersion)" }
        if (@($p.SkippedDrafts).Count -ne 1 -or $p.SkippedDrafts[0] -notlike '*id 7*') { throw 'пропущена чернетка не названа' }
        $vers2 = $vers + @([pscustomobject]@{ id = 8; version = '1.0.15.0'; status = 'Draft' })
        if ((Get-LandTemplateDraftPlan -Versions $vers2).NewVersion -ne '1.0.16.0') { throw 'зайнятий номер 1.0.15.0 мусить обходитися' }
        if ((Get-LandTemplateDraftPlan -Versions @($vers[0], $vers[1])).Action -ne 'Clone') { throw 'без чернетки теж свіжий клон' }
    }

    It 'виконання: один POST clone з опублікованої; до старої чернетки (id 7) жодного запиту; відмова сервера - дослівно, без видалень' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/EcrApi.ps1')
        . (Join-Path $repo 'tools/land/LandTemplateDraft.Lib.ps1')
        Set-StrictMode -Off
        $script:Calls = New-Object System.Collections.ArrayList
        $script:Reply = 201
        function Invoke-Ecr {
            param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{})
            [void]$script:Calls.Add("$Method $Path $(if ($Body) { $Body.newVersion })")
            if ($script:Reply -eq 201) { return [pscustomobject]@{ Status = 201; Json = [pscustomobject]@{ versionId = 9 }; Text = ''; Headers = @{} } }
            return [pscustomobject]@{ Status = 409; Json = [pscustomobject]@{ errorCode = 'ECR-TMPL-0409' }; Text = 'one draft'; Headers = @{} }
        }
        $vers = @([pscustomobject]@{ id = 5; version = '1.0.14.0'; status = 'Published' }, [pscustomobject]@{ id = 7; version = '1.1.1.9999'; status = 'Draft' })
        $id = New-LandTemplateCloneDraft -Plan (Get-LandTemplateDraftPlan -Versions $vers)
        if ($id -ne 9) { throw "id нової чернетки $id" }
        if (($script:Calls -join ';') -ne 'POST /api/v1/template-versions/5/clone 1.0.15.0') { throw "запити: $($script:Calls -join ';')" }
        $script:Calls.Clear(); $script:Reply = 409
        $msg = $null
        try { $null = New-LandTemplateCloneDraft -Plan (Get-LandTemplateDraftPlan -Versions $vers) } catch { $msg = $_.Exception.Message }
        if ($msg -notlike '*HTTP 409 ECR-TMPL-0409 one draft*' -or $msg -notlike '*не змінено й не видалено*') { throw "відмова: $msg" }
        if (@($script:Calls | Where-Object { $_ -notlike 'POST */clone*' }).Count -ne 0) { throw 'після відмови - інші запити' }
    }

    It '-ReuseDraft: стара поведінка (наявна чернетка; опублікована без чернетки - структуру не чіпати); Stage-Template бере план' {
        $repo = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
        . (Join-Path $repo 'tools/land/LandTemplateDraft.Lib.ps1')
        $vers = @([pscustomobject]@{ id = 5; version = '1.0.14.0'; status = 'Published' }, [pscustomobject]@{ id = 7; version = '1.1.1.9999'; status = 'Draft' })
        $u = Get-LandTemplateDraftPlan -Versions $vers -ReuseDraft
        if ($u.Action -ne 'UseDraft' -or $u.DraftId -ne 7) { throw '-ReuseDraft мусить брати наявну чернетку' }
        if ((Get-LandTemplateDraftPlan -Versions @($vers[0]) -ReuseDraft).Action -ne 'PublishedNoOp') { throw '-ReuseDraft без чернетки' }
        $ast = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'tools/land/Build-LandTemplate.ps1'), [ref]$null, [ref]$null)
        $par = $ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'ReuseDraft' }
        if ($null -eq $par -or $par.StaticType -ne [switch] -or $null -ne $par.DefaultValue) { throw '-ReuseDraft мусить бути перемикачем, типово вимкненим' }
        $st = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Stage-Template' }, $true).Extent.Text
        if ($st -notlike '*Get-LandTemplateDraftPlan -Versions $vers -ReuseDraft:$ReuseDraft*' -or $st -notlike '*New-LandTemplateCloneDraft -Plan $plan*') { throw 'Stage-Template не бере план' }
    }
}
