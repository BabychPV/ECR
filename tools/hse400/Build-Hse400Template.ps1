# tools/hse400/Build-Hse400Template.ps1
# Шаблон HSE400 "Компонентний склад газу" (річний, місяці в колонках M01..M12) і річний проєкт із одним документом.
#
#   -Stage Template : шаблон HSE400, версія з hse400-model.json, аркуш, 6 таблиць, 12 місячних колонок, фіксовані рядки
#                     (ввід + розрахункові F_<X> лише для читання), публікація;
#   -Stage Project  : політика періодів, річний проєкт HSE400_<рік>, активація, один документ;
#   -Stage All      : обидва.
#
# Ідемпотентний: опублікована версія не змінюється, наявні проєкт і документ не створюються вдруге.
# -PreviewOnly: лише GET, показує, що було б зроблено.
# Облікових записів, ролей і баз не створює. Стенд - лише локальний і лише з -ConfirmStand <база>.
[CmdletBinding()]
param(
    [ValidateSet('Template', 'Project', 'All')][string]$Stage = 'All',
    [int]$Year = (Get-Date).Year,
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$ConfirmStand,
    [Parameter(Mandatory)][string]$CredentialFile,
    [switch]$PreviewOnly
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Hse400Stand.Lib.ps1')
. (Join-Path $PSScriptRoot 'Hse400Model.Lib.ps1')

function Get-Hse400Id {
    <# Перше наявне з перелічених полів відповіді. #>
    param($Json, [string[]]$Names)
    foreach ($n in $Names) { if ($null -ne $Json -and $Json.PSObject.Properties[$n] -and $null -ne $Json.$n) { return $Json.$n } }
    throw "У відповіді немає жодного з полів: $($Names -join ', ')."
}

function Find-Hse400Template {
    param([string]$Code)
    $r = Invoke-Hse400Ok -Method GET -Path ("/api/v1/templates?limit=50&q={0}" -f $Code)
    return (Get-Hse400Items $r.Json | Where-Object { [string]$_.code -eq $Code } | Select-Object -First 1)
}

function Get-Hse400Versions {
    param($TemplateId)
    $r = Invoke-Hse400Ok -Method GET -Path ("/api/v1/templates/{0}/versions?limit=100" -f $TemplateId)
    return @(Get-Hse400Items $r.Json)
}

Set-Hse400Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly
$model = Read-Hse400Model
$months = @(Get-Hse400MonthColumns)
$tplCode = [string]$model.template.code
$tplVersion = [string]$model.template.version
$projectCode = '{0}_{1}' -f $tplCode, $Year
$policyCode = 'HSE400_OPEN'
$state = Read-Hse400State
Connect-Hse400
Write-Hse400Log ("Стенд {0}; шаблон {1} {2}; етап {3}{4}" -f (Get-Hse400StandName), $tplCode, $tplVersion, $Stage, $(if ($PreviewOnly) { ' (лише перегляд)' } else { '' }))

# ---------- перегляд ----------
if ($PreviewOnly) {
    $t = Find-Hse400Template $tplCode
    if ($null -eq $t) { Write-Hse400Log "Шаблону $tplCode немає - було б створено разом із версією $tplVersion." }
    else {
        $vs = Get-Hse400Versions (Get-Hse400Id $t 'id', 'templateId')
        $pub = @($vs | Where-Object { [string]$_.version -eq $tplVersion -and [string]$_.status -eq 'Published' })
        $dr = @($vs | Where-Object { [string]$_.version -eq $tplVersion -and [string]$_.status -eq 'Draft' })
        if ($pub.Count) { Write-Hse400Log "Версію $tplVersion опубліковано - структуру не чіпаю." }
        elseif ($dr.Count) { Write-Hse400Log "Є чернетка $tplVersion - було б дозаповнено й опубліковано." }
        else { Write-Hse400Log "Версії $tplVersion немає - було б створено чернетку й опубліковано." }
    }
    $rows = 0; foreach ($tb in @($model.tables)) { $rows += @($tb.rows).Count }
    Write-Hse400Log ("Модель: таблиць {0}, рядків {1}, колонок на таблицю {2}." -f @($model.tables).Count, $rows, $months.Count)
    $p = Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path '/api/v1/projects?limit=200').Json | Where-Object { [string]$_.code -eq $projectCode } | Select-Object -First 1
    if ($null -eq $p) { Write-Hse400Log "Проєкту $projectCode немає - було б створено з одним документом." }
    else { Write-Hse400Log "Проєкт $projectCode уже є." }
    return
}

# ---------- шаблон ----------
if ($Stage -in 'Template', 'All') {
    $t = Find-Hse400Template $tplCode
    if ($null -eq $t) {
        $r = Invoke-Hse400Ok -Method POST -Path '/api/v1/templates' -Body @{ code = $tplCode; nameL10n = @{ en = [string]$model.template.en; ru = [string]$model.template.ru } }
        $templateId = Get-Hse400Id $r.Json 'id', 'templateId'
        Write-Hse400Log "Створено шаблон $tplCode (id $templateId)."
    }
    else { $templateId = Get-Hse400Id $t 'id', 'templateId' }
    $vs = Get-Hse400Versions $templateId
    $pub = $vs | Where-Object { [string]$_.version -eq $tplVersion -and [string]$_.status -eq 'Published' } | Select-Object -First 1
    if ($null -ne $pub) {
        $vid = Get-Hse400Id $pub 'id', 'templateVersionId'
        Write-Hse400Log "Версію $tplVersion уже опубліковано (id $vid) - структуру не чіпаю."
    }
    else {
        $dr = $vs | Where-Object { [string]$_.version -eq $tplVersion -and [string]$_.status -eq 'Draft' } | Select-Object -First 1
        if ($null -ne $dr) { $vid = Get-Hse400Id $dr 'id', 'templateVersionId' }
        else {
            $r = Invoke-Hse400Ok -Method POST -Path ("/api/v1/templates/{0}/versions" -f $templateId) -Body @{ versionNumber = $tplVersion; cloneFromVersionId = $null }
            $vid = Get-Hse400Id $r.Json 'id', 'templateVersionId'
        }
        Write-Hse400Log "Чернетка $tplVersion (id $vid)."
        $sheet = [string]$model.sheet.code
        Invoke-Hse400Ok -Method PUT -Path ("/api/v1/template-versions/{0}/sheets/{1}" -f $vid, $sheet) -Body @{
            nameL10n = @{ en = [string]$model.sheet.en; ru = [string]$model.sheet.ru }; ordinal = 1; sheetGroup = $null; isMandatory = $true; isVisible = $true
        } | Out-Null
        $tOrd = 0
        foreach ($tb in @($model.tables)) {
            $tOrd++
            $r = Invoke-Hse400Ok -Method PUT -Path ("/api/v1/template-versions/{0}/sheets/{1}/tables/{2}" -f $vid, $sheet, $tb.code) -Body @{
                nameL10n = @{ en = [string]$tb.en; ru = [string]$tb.ru }; ordinal = $tOrd; layoutKind = 'PerPeriodInstance'; rowMode = 'Fixed'; maxDynamicRows = $null
            }
            $tid = Get-Hse400Id $r.Json 'id', 'tableDefId'
            foreach ($m in $months) {
                Invoke-Hse400Ok -Method PUT -Path ("/api/v1/template-versions/{0}/tables/{1}/columns/{2}" -f $vid, $tid, $m.Code) -Body @{
                    headerL10n = @{ en = $m.En; ru = $m.Ru }; ordinal = $m.Month; dataType = 'Decimal'
                    isRequired = $false; isReadOnly = $false; isHidden = $false; precision = $null; scale = $null
                    defaultValue = $null; displayFormat = $null; styleId = $null; lookupRegistryDefId = $null; lookupFilter = $null
                    unitId = $null; widthPx = 110; isMonthColumn = $true; monthNumber = $m.Month
                } | Out-Null
            }
            $rOrd = 0
            foreach ($row in @($tb.rows)) {
                $rOrd++
                Invoke-Hse400Ok -Method PUT -Path ("/api/v1/template-versions/{0}/tables/{1}/rows/{2}" -f $vid, $tid, $row.key) -Body @{
                    labelL10n = @{ en = [string]$row.en; ru = [string]$row.ru }; ordinal = $rOrd; rowKind = 'Item'; parentRowKey = $null
                    isReadOnly = ([string]$row.kind -eq 'Calc')
                } | Out-Null
            }
            Write-Hse400Log ("  {0}: колонок {1}, рядків {2}." -f $tb.code, $months.Count, $rOrd)
        }
        Invoke-Hse400Ok -Method POST -Path ("/api/v1/template-versions/{0}/publish" -f $vid) -Body @{ reason = "HSE400 $tplVersion" } | Out-Null
        Write-Hse400Log "Версію $tplVersion опубліковано."
    }
    $state['templateId'] = $templateId; $state['templateVersionId'] = $vid
    Save-Hse400State $state
}

# ---------- проєкт і документ ----------
if ($Stage -in 'Project', 'All') {
    if (-not $state.ContainsKey('templateVersionId')) { throw 'У стані немає templateVersionId: спершу -Stage Template на цьому стенді.' }
    $vid = $state['templateVersionId']
    $st = (Invoke-Hse400Ok -Method GET -Path ("/api/v1/template-versions/{0}/structure" -f $vid)).Json
    $sheetDef = @($st.sheets) | Where-Object { [string]$_.code -eq [string]$model.sheet.code } | Select-Object -First 1
    if ($null -eq $sheetDef) { throw "У версії $vid немає аркуша $($model.sheet.code)." }

    $pol = Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path '/api/v1/projects/period-policies').Json | Where-Object { [string]$_.code -eq $policyCode } | Select-Object -First 1
    if ($null -eq $pol) {
        $pol = (Invoke-Hse400Ok -Method POST -Path '/api/v1/projects/period-policies' -Body @{
                code = $policyCode; openOffsetDays = 0; graceOffsetDays = 800; hardCloseOffsetDays = 900; yearGraceOffsetDays = 900 }).Json
        Write-Hse400Log "Створено політику періодів $policyCode."
    }
    $policyId = Get-Hse400Id $pol 'id', 'periodPolicyId'

    $proj = Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path '/api/v1/projects?limit=200').Json | Where-Object { [string]$_.code -eq $projectCode } | Select-Object -First 1
    if ($null -eq $proj) {
        $proj = (Invoke-Hse400Ok -Method POST -Path '/api/v1/projects' -Body @{
                code = $projectCode; nameL10n = @{ en = "HSE400 $Year"; ru = "HSE400 $Year" }; timeZoneId = 'Asia/Atyrau'
                periodKind = 'Yearly'; year = $Year; templateVersionId = $vid; periodPolicyId = $policyId; customPeriodCount = $null }).Json
        $projectId = Get-Hse400Id $proj 'id', 'projectId'
        Invoke-Hse400Ok -Method POST -Path ("/api/v1/projects/{0}/activate" -f $projectId) | Out-Null
        Write-Hse400Log "Створено й активовано проєкт $projectCode (id $projectId)."
    }
    else { $projectId = Get-Hse400Id $proj 'id', 'projectId'; Write-Hse400Log "Проєкт $projectCode уже є (id $projectId)." }

    $periods = @((Invoke-Hse400Ok -Method GET -Path ("/api/v1/projects/{0}/periods" -f $projectId)).Json.periods)
    if (-not $periods.Count) { throw "Проєкт $projectCode не має періодів." }
    $periodKey = $periods[0].periodKey

    $doc = Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path ("/api/v1/documents?limit=200&projectId={0}" -f $projectId)).Json | Select-Object -First 1
    if ($null -eq $doc) {
        $doc = (Invoke-Hse400Ok -Method POST -Path '/api/v1/documents' -Body @{
                name = @{ en = "HSE400 $Year" }; projectId = $projectId; sheetDefIds = @($sheetDef.id); templateVersionId = $vid }).Json
        Write-Hse400Log "Створено документ."
    }
    $documentId = Get-Hse400Id $doc 'id', 'documentId'

    $inst = @{}
    foreach ($ti in Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path ("/api/v1/documents/{0}/tables?periodKey={1}" -f $documentId, $periodKey)).Json) {
        $inst[[string]$ti.tableCode] = $ti.tableInstanceId
    }
    $state['projectId'] = $projectId; $state['documentId'] = $documentId; $state['periodKey'] = $periodKey; $state['tableInstances'] = $inst
    Save-Hse400State $state
    Write-Hse400Log ("Документ {0}, період {1}, таблиць {2}. Стан: {3}" -f $documentId, $periodKey, $inst.Count, (Get-Hse400StateFile))
}
