<#
.SYNOPSIS
    Крок 5 HSE401: шаблон HSE401 (лист HSE, одна динамічна таблиця ~62 колонки, групи заголовків у 2 рівні, формули),
    публікація, ПРОЄКТ Yearly 2026 і порожній ДОКУМЕНТ. Стан (id) -> out/hse401-state.json.

.PARAMETER Stage
    Template | Project | All (за замовчуванням All). Повтор безпечний: наявне не перестворюється.
#>
[CmdletBinding()]
param([ValidateSet('Template', 'Project', 'All')][string]$Stage = 'All', [int]$Year = 2026,
    [ValidateSet('Scratch', 'Live')][string]$Target = 'Scratch', [string]$BaseUrl, [string]$Database, [string]$ConfirmStand, [string]$CredentialFile,
    [switch]$PreviewOnly, [switch]$AcceptNaePreview)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
. (Join-Path $here 'Hse401Model.Lib.ps1')
Set-Hse401Target -Target $Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly -AcceptNaePreview:$AcceptNaePreview
$preview = Test-Hse401Preview
$out = Get-Hse401OutDir
$stateFile = Get-Hse401StateFile
function Get-State { if (Test-Path $stateFile) { $s0 = Read-HseJson $stateFile; Assert-Hse401StateMatches $s0; return $s0 } else { return @{} } }
function Save-State($s) { $s['standKey'] = Get-Hse401StandKey; [IO.File]::WriteAllText($stateFile, (ConvertTo-HseJson -Value $s).ToString(), (New-Object Text.UTF8Encoding($false))) }
$state = Get-State

Connect-Hse
$code = $script:HseTemplateCode

if ($preview) {
    # лише GET: що вже існує на стенді і що було б створено
    if ($Stage -in 'Template', 'All') {
        $t = (Invoke-HseOk GET ("/api/v1/templates?limit=50&q=$code")).Json
        $tpl = @($t.items) | Where-Object { $_.code -eq $code } | Select-Object -First 1
        $pubV = $null; $drfV = $null
        if ($null -ne $tpl) {
            $vers = @((Invoke-HseOk GET "/api/v1/templates/$($tpl.id)/versions?limit=100").Json.items)
            $pub = $vers | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1
            $drf = $vers | Where-Object { $_.status -eq 'Draft' } | Sort-Object id -Descending | Select-Object -First 1
            if ($null -ne $pub) { $pubV = [string]$pub.version }
            if ($null -ne $drf) { $drfV = [string]$drf.version }
        }
        $cols = Get-Hse401Columns
        Save-Hse401PlanSection 'template' ([ordered]@{
                templateCode = $code; templateExists = ($null -ne $tpl); publishedVersion = $pubV; draftVersion = $drfV
                wouldCreateTemplate = ($null -eq $tpl); wouldBuildAndPublishVersion = ($null -eq $pubV -or $null -ne $drfV)
                columns = $cols.Count; formulas = @($cols | Where-Object { $_.Kind -eq 'Formula' }).Count
            })
        Write-HseLog ("[preview] шаблон {0}: існує={1}, опублікована={2}, чернетка={3}" -f $code, ($null -ne $tpl), $pubV, $drfV)
    }
    if ($Stage -in 'Project', 'All') {
        $pols = Get-HseItems (Invoke-HseOk GET '/api/v1/projects/period-policies').Json
        $pol = $pols | Where-Object { $_.code -eq 'HSE401_OPEN' } | Select-Object -First 1
        $found = Find-Hse401Document
        Save-Hse401PlanSection 'project' ([ordered]@{
                policyCode = 'HSE401_OPEN'; policyExists = ($null -ne $pol); projectCode = 'HSE401_2026'; projectExists = ($null -ne $found)
                documentExists = ($null -ne $found -and $null -ne $found['documentId']); documentId = $(if ($null -ne $found) { $found['documentId'] } else { $null })
                wouldCreatePolicy = ($null -eq $pol); wouldCreateProject = ($null -eq $found); wouldCreateDocument = ($null -eq $found -or $null -eq $found['documentId'])
            })
        Write-HseLog ("[preview] політика HSE401_OPEN існує={0}; проєкт HSE401_2026 існує={1}; документ існує={2}" -f ($null -ne $pol), ($null -ne $found), ($null -ne $found -and $null -ne $found['documentId']))
    }
    return
}

if ($Stage -in 'Template', 'All') {
    Write-HseLog '== Template =='
    $regIds = @{}
    foreach ($r in @((Invoke-HseOk GET '/api/v1/registries').Json)) { $regIds[[string]$r.code] = [int]$r.id }
    $t = (Invoke-HseOk GET ("/api/v1/templates?limit=50&q=$code")).Json
    $tpl = @($t.items) | Where-Object { $_.code -eq $code } | Select-Object -First 1
    if ($null -eq $tpl) {
        $null = Invoke-HseOk POST '/api/v1/templates' @{ code = $code; nameL10n = @{ en = 'HSE401 Air emissions inventory'; ru = 'HSE401 Инвентаризация выбросов в атмосферу' } }
        $t = (Invoke-HseOk GET ("/api/v1/templates?limit=50&q=$code")).Json
        $tpl = @($t.items) | Where-Object { $_.code -eq $code } | Select-Object -First 1
    }
    $vers = @((Invoke-HseOk GET "/api/v1/templates/$($tpl.id)/versions?limit=100").Json.items)
    $pub = $vers | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1
    $draft = $vers | Where-Object { $_.status -eq 'Draft' } | Sort-Object id -Descending | Select-Object -First 1
    if ($null -ne $pub -and $null -eq $draft) {
        Write-HseLog "  шаблон уже опублікований (версія $($pub.version), id $($pub.id)); структуру не чіпаю"
        $state['templateId'] = [int]$tpl.id; $state['versionId'] = [int]$pub.id
    }
    else {
        if ($null -eq $draft) {
            $null = Invoke-HseOk POST "/api/v1/templates/$($tpl.id)/versions" @{ versionNumber = '1.0.0.1'; cloneFromVersionId = $null }
            $vers = @((Invoke-HseOk GET "/api/v1/templates/$($tpl.id)/versions?limit=100").Json.items)
            $draft = $vers | Where-Object { $_.status -eq 'Draft' } | Sort-Object id -Descending | Select-Object -First 1
        }
        $vid = [int]$draft.id
        Write-HseLog "  чернетка $($draft.version) (id $vid)"
        $null = Invoke-HseOk PUT "/api/v1/template-versions/$vid/sheets/$($script:HseSheetCode)" @{ nameL10n = (ConvertTo-HseL10n 'HSE' 'HSE'); ordinal = 1; sheetGroup = $null; isMandatory = $true; isVisible = $true }
        $tb = (Invoke-HseOk PUT "/api/v1/template-versions/$vid/sheets/$($script:HseSheetCode)/tables/$($script:HseTableCode)" @{
                nameL10n = (ConvertTo-HseL10n 'Air emissions inventory' 'Инвентаризация выбросов'); ordinal = 1; layoutKind = 'PerPeriodInstance'; rowMode = 'Dynamic'; maxDynamicRows = $null }).Json
        $tid = if ($tb.id) { [int]$tb.id } else { [int]$tb.tableDefId }
        Write-HseLog "  таблиця $($script:HseTableCode) id $tid"
        $cols = Get-Hse401Columns
        $colIds = @{}
        $ord = 0
        foreach ($c in $cols) {
            $ord++
            $lookup = $null
            if ($c.Type -eq 'Lookup') { if (-not $regIds.ContainsKey($c.Registry)) { throw "Колонка $($c.Code): немає довідника $($c.Registry)" }; $lookup = $regIds[$c.Registry] }
            $dt = if ($c.Kind -eq 'Formula') { 'Formula' } else { $c.Type }
            $body = @{
                headerL10n = (ConvertTo-HseL10n $c.En $c.Ru); ordinal = $ord; dataType = $dt; isRequired = $false; isReadOnly = ($c.Kind -eq 'Formula')
                isHidden = [bool]$c.Hidden; precision = $(if ($null -ne $c.Precision -and $dt -eq 'Decimal') { [int]$c.Precision } else { $null }); scale = $(if ($null -ne $c.Scale -and $dt -eq 'Decimal') { [int]$c.Scale } else { $null })
                defaultValue = $null; displayFormat = $(if ($c.DisplayFormat) { [string]$c.DisplayFormat } else { $null }); styleId = $null; lookupRegistryDefId = $lookup; lookupFilter = $c.Filter; unitId = $null
                widthPx = $(if ($c.Width -gt 0) { [int]$c.Width } else { $null }); isMonthColumn = $false; monthNumber = $null }
            $cr = Invoke-HseOk PUT "/api/v1/template-versions/$vid/tables/$tid/columns/$($c.Code)" $body
            $colIds[$c.Code] = $(if ($cr.Json.id) { [int]$cr.Json.id } else { [int]$cr.Json.columnDefId })
        }
        Write-HseLog "  колонок: $($cols.Count)"
        $nf = 0
        foreach ($c in $cols) {
            if ($c.Kind -ne 'Formula') { continue }
            $null = Invoke-HseOk PUT "/api/v1/template-versions/$vid/tables/$tid/formulas/column/$($colIds[$c.Code])" @{ dialect = 'Template'; expression = $c.Expr }
            $nf++
        }
        Write-HseLog "  формул: $nf"
        # групи заголовків
        $groups = Get-Hse401Groups
        $gb = @($groups | ForEach-Object { @{ code = $_.Code; parentCode = $_.Parent; ordinal = $_.Ordinal; labelL10n = (ConvertTo-HseL10n $_.En $_.Ru) } })
        $gr = Invoke-HseOk PUT "/api/v1/template-versions/$vid/tables/$tid/header-groups" @{ groups = $gb }
        $gid = @{}
        foreach ($g in @($gr.Json.groups)) { $gid[[string]$g.code] = [int]$g.id }
        $changes = @()
        foreach ($c in $cols) { if ($c.Group) { $changes += @{ entityType = 'ColumnDef'; entityId = [int]$colIds[$c.Code]; field = 'HeaderGroupId'; value = [string]$gid[$c.Group] } } }
        $null = Invoke-HseOk PATCH "/api/v1/template-versions/$vid/presentation" $changes
        Write-HseLog "  груп заголовків: $($groups.Count); колонок прив'язано: $($changes.Count)"
        $p = Invoke-Hse POST "/api/v1/template-versions/$vid/publish" @{ reason = $(if (Test-Hse401Live) { 'HSE401: перша версія шаблону (завантаження з книги HSE401_v1.0.3.1)' } else { 'HSE401 scratch: перша версія' }) }
        if ($p.Status -notin 200, 201, 202, 204) { throw "publish -> $($p.Status) $(Get-EcrErrorCode $p) $($p.Text)" }
        Write-HseLog "  опубліковано версію id $vid"
        $state['templateId'] = [int]$tpl.id; $state['versionId'] = $vid; $state['tableDefId'] = $tid
        $state['columnIds'] = $colIds
    }
    Save-State $state
}

if ($Stage -in 'Project', 'All') {
    Write-HseLog '== Project / Document =='
    $vid = [int]$state['versionId']
    $st = (Invoke-HseOk GET "/api/v1/template-versions/$vid/structure").Json
    $sheet = @($st.sheets) | Where-Object { $_.code -eq $script:HseSheetCode } | Select-Object -First 1
    $table = @($sheet.tables) | Where-Object { $_.code -eq $script:HseTableCode } | Select-Object -First 1
    $state['sheetDefId'] = [int]$sheet.id; $state['tableDefId'] = [int]$table.id
    $cid = @{}; foreach ($col in @($table.columns)) { $cid[[string]$col.code] = [int]$col.id }
    $state['columnIds'] = $cid

    $pols = Get-HseItems (Invoke-HseOk GET '/api/v1/projects/period-policies').Json
    $pol = $pols | Where-Object { $_.code -eq 'HSE401_OPEN' } | Select-Object -First 1
    if ($null -eq $pol) {
        $pol = (Invoke-HseOk POST '/api/v1/projects/period-policies' @{ code = 'HSE401_OPEN'; openOffsetDays = 0; graceOffsetDays = 800; hardCloseOffsetDays = 900; yearGraceOffsetDays = 900 }).Json
    }
    $projs = Get-HseItems (Invoke-HseOk GET '/api/v1/projects').Json
    $prj = $projs | Where-Object { $_.code -eq 'HSE401_2026' } | Select-Object -First 1
    if ($null -eq $prj) {
        $null = Invoke-HseOk POST '/api/v1/projects' @{ code = 'HSE401_2026'; nameL10n = @{ en = "HSE401 inventory $Year" }; timeZoneId = 'Asia/Atyrau'; periodKind = 'Yearly'; year = $Year
            templateVersionId = $vid; periodPolicyId = [int]$pol.id; customPeriodCount = $null }
        $projs = Get-HseItems (Invoke-HseOk GET '/api/v1/projects').Json
        $prj = $projs | Where-Object { $_.code -eq 'HSE401_2026' } | Select-Object -First 1
        Write-HseLog "  проєкт HSE401_2026 id $($prj.id)"
        $act = Invoke-Hse POST "/api/v1/projects/$($prj.id)/activate"
        Write-HseLog "  активація: HTTP $($act.Status) $(Get-EcrErrorCode $act) $(if ($act.Status -ge 400) { $act.Text })"
    }
    $state['projectId'] = [int]$prj.id
    $per = (Invoke-HseOk GET "/api/v1/projects/$($prj.id)/periods").Json
    Write-HseLog ('  періоди: ' + ((@($per.periods) | ForEach-Object { "$($_.periodKey)=$($_.state)" }) -join ' '))
    $state['periodKey'] = [int](@($per.periods) | Select-Object -First 1).periodKey

    $docs = Get-HseItems (Invoke-HseOk GET "/api/v1/documents?limit=200&projectId=$($prj.id)").Json
    $doc = $docs | Where-Object { $_.projectId -eq $prj.id } | Select-Object -First 1
    if ($null -eq $doc) {
        $d = Invoke-HseOk POST '/api/v1/documents' @{ name = @{ en = "HSE401 $Year" }; projectId = [int]$prj.id; sheetDefIds = @([int]$sheet.id); templateVersionId = $vid }
        $docId = $(if ($d.Json.PSObject.Properties['id']) { [int]$d.Json.id } else { [int]$d.Json.documentId })
        Write-HseLog "  документ id $docId"
    }
    else { $docId = [int]$doc.id; Write-HseLog "  документ вже є: id $docId" }
    $state['documentId'] = $docId
    $tabs = (Invoke-HseOk GET "/api/v1/documents/$docId/tables?periodKey=$($state['periodKey'])").Json
    $ti = @($tabs) | Where-Object { $_.tableCode -eq $script:HseTableCode } | Select-Object -First 1
    $state['tableInstanceId'] = [int]$ti.tableInstanceId
    Write-HseLog "  tableInstanceId $($ti.tableInstanceId), periodKey $($state['periodKey'])"
    Save-State $state
}
