# tools/hse400/Build-Hse400Template.ps1
# Шаблон HSE400 "Компонентний склад газу" (річний, місяці в колонках M01..M12), методологія HSE400 для модуля
# місячних колонок і річний проєкт з одним документом.
#
#   -Stage Template : перевірка одиниці 'one'; шаблон HSE400, версія з hse400-model.json, аркуш, 6 таблиць
#                     MonthsInColumns, 12 місячних колонок + прихована колонка-якір HSE400_Calc (Calculated),
#                     фіксовані рядки (ввід + розрахункові F_<X> лише для читання), публікація;
#                     методологія HSE400: версія рівня Module, правило рядків "{}", формули/константи з моделі;
#                     золоті тести: окремий проєкт HSE400_GOLD з одним документом, у який пишеться синтетичний
#                     ввід тестів (модуль читає комірки документа, а не аргументи тесту); публікація (лише коли
#                     модель покриває всі F_<X> і має тест), активні прив'язки на якір кожної з 6 таблиць;
#   -Stage Project  : політика періодів, річний проєкт HSE400_<рік>, активація, один документ;
#   -Stage All      : обидва.
#
# Ідемпотентний: опубліковані версії не змінюються, наявні методологія, проєкт і документ не створюються вдруге,
# наявна активна прив'язка не перезаписується.
# Код виходу 2: щось не доведено до кінця (напр. у моделі немає формул) - причини в попередженнях.
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
. (Join-Path $PSScriptRoot 'Hse400Methodology.Lib.ps1')

$script:Incomplete = New-Object System.Collections.Generic.List[string]

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
    <# TemplateVersionSummary: id, version, status. #>
    param($TemplateId)
    return @(Get-Hse400AllItems -Path ("/api/v1/templates/{0}/versions" -f $TemplateId) -Limit 100)
}

function Get-Hse400Structure {
    param($VersionId)
    return (Invoke-Hse400Ok -Method GET -Path ("/api/v1/template-versions/{0}/structure" -f $VersionId)).Json
}

function Find-Hse400Methodology {
    <# GET /api/v1/methodologies (MethodologyDto[]: id, code; без ids - усі активні, і чернетки теж). #>
    param([string]$Code)
    return (@(Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path '/api/v1/methodologies').Json) |
            Where-Object { [string]$_.code -eq $Code } | Select-Object -First 1)
}

function Get-Hse400MethodologyVersions {
    <# MethodologyDraftVersionDto[]: id, versionNumber, status, level. #>
    param($MethodologyId)
    return @(Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path ("/api/v1/methodologies/{0}/versions" -f $MethodologyId)).Json)
}

function Get-Hse400Bindings {
    <# CalculationBindingDto[]: columnDefId, outputCode, matchJson, isActive. #>
    param($MethodologyId)
    return @(Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path ("/api/v1/methodologies/{0}/bindings" -f $MethodologyId)).Json)
}

function Add-Hse400Incomplete { param([string]$Message) $script:Incomplete.Add($Message); Write-Hse400Log "! $Message" }

function Get-Hse400PolicyId {
    <# Політика періодів (наявна за кодом або нова). #>
    param([string]$Code)
    $pol = Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path '/api/v1/projects/period-policies').Json | Where-Object { [string]$_.code -eq $Code } | Select-Object -First 1
    if ($null -eq $pol) {
        $pol = (Invoke-Hse400Ok -Method POST -Path '/api/v1/projects/period-policies' -Body @{
                code = $Code; openOffsetDays = 0; graceOffsetDays = 800; hardCloseOffsetDays = 900; yearGraceOffsetDays = 900 }).Json
        Write-Hse400Log "Створено політику періодів $Code."
    }
    return (Get-Hse400Id $pol 'id', 'periodPolicyId')
}

function Get-Hse400ProjectDocument {
    <# Річний проєкт з одним документом на версії шаблону: наявні не створюються вдруге. Повертає адреси документа. #>
    param([Parameter(Mandatory)][string]$Code, [Parameter(Mandatory)][string]$Title, [Parameter(Mandatory)][int]$ProjectYear,
        [Parameter(Mandatory)]$VersionId, [Parameter(Mandatory)]$PolicyId)
    $st = Get-Hse400Structure $VersionId
    $sheetDef = @($st.sheets) | Where-Object { [string]$_.code -eq [string]$model.sheet.code } | Select-Object -First 1
    if ($null -eq $sheetDef) { throw "У версії $VersionId немає аркуша $($model.sheet.code)." }

    $proj = Get-Hse400AllItems -Path '/api/v1/projects' | Where-Object { [string]$_.code -eq $Code } | Select-Object -First 1
    if ($null -eq $proj) {
        $proj = (Invoke-Hse400Ok -Method POST -Path '/api/v1/projects' -Body @{
                code = $Code; nameL10n = @{ en = $Title; ru = $Title }; timeZoneId = 'Asia/Atyrau'
                periodKind = 'Yearly'; year = $ProjectYear; templateVersionId = $VersionId; periodPolicyId = $PolicyId; customPeriodCount = $null }).Json
        $projectId = Get-Hse400Id $proj 'projectId', 'id'
        Invoke-Hse400Ok -Method POST -Path ("/api/v1/projects/{0}/activate" -f $projectId) | Out-Null
        Write-Hse400Log "Створено й активовано проєкт $Code (id $projectId)."
    }
    else { $projectId = Get-Hse400Id $proj 'id', 'projectId'; Write-Hse400Log "Проєкт $Code уже є (id $projectId)." }

    $periods = @((Invoke-Hse400Ok -Method GET -Path ("/api/v1/projects/{0}/periods" -f $projectId)).Json.periods)
    if (-not $periods.Count) { throw "Проєкт $Code не має періодів." }
    $periodKey = $periods[0].periodKey

    $doc = Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path ("/api/v1/documents?limit=200&projectId={0}" -f $projectId)).Json | Select-Object -First 1
    if ($null -eq $doc) {
        $doc = (Invoke-Hse400Ok -Method POST -Path '/api/v1/documents' -Body @{
                name = @{ en = $Title }; projectId = $projectId; sheetDefIds = @($sheetDef.id); templateVersionId = $VersionId }).Json
        Write-Hse400Log "Створено документ проєкту $Code."
    }
    $documentId = Get-Hse400Id $doc 'documentId', 'id'
    Sync-Hse400DocumentVersion -DocumentId $documentId -VersionId $VersionId -Code $Code -PeriodKey $periodKey

    $inst = @{}
    foreach ($ti in Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path ("/api/v1/documents/{0}/tables?periodKey={1}" -f $documentId, $periodKey)).Json) {
        $inst[[string]$ti.tableCode] = $ti.tableInstanceId
    }
    return @{ ProjectId = $projectId; DocumentId = $documentId; PeriodKey = $periodKey; Instances = $inst }
}

function Sync-Hse400DocumentVersion {
    <# Наявний проєкт на старішій версії шаблону переноситься на поточну (ФВ-7.5, режим Safe; переносяться всі
       документи проєкту). Спершу сухий прогін: відмова або втрата введених значень - зупинка, нічого не записано. #>
    param([Parameter(Mandatory)]$DocumentId, [Parameter(Mandatory)]$VersionId, [Parameter(Mandatory)][string]$Code,
        [Parameter(Mandatory)]$PeriodKey)
    $targets = (Invoke-Hse400Ok -Method GET -Path ("/api/v1/documents/{0}/migrate-version" -f $DocumentId)).Json
    if ([int]$targets.currentVersionId -eq [int]$VersionId) { return }
    $path = "/api/v1/documents/{0}/migrate-version" -f $DocumentId
    $dry = (Invoke-Hse400Ok -Method POST -Path $path -Body (New-Hse400MigrateBody -TargetVersionId $VersionId -DryRun)).Json
    Assert-Hse400MigrationPlan -Plan $dry -Code $Code
    $done = (Invoke-Hse400Ok -Method POST -Path $path -Body (New-Hse400MigrateBody -TargetVersionId $VersionId)).Json
    if (-not $done.applied) { throw "Перенос проєкту $Code на версію шаблону $VersionId не застосовано." }
    Write-Hse400Log ("Проєкт {0} перенесено з версії шаблону {1} на {2}: документів {3}, значень {4}." -f $Code, $targets.currentVersionId, $VersionId, $done.documentCount, $done.transferredValues)
    # Після переносу документ ще якийсь час «переїжджає» (409 ECR-DOC-4091): чекаємо, а не падаємо.
    $probe = "/api/v1/documents/{0}/tables?periodKey={1}" -f $DocumentId, $PeriodKey
    for ($i = 0; $i -lt 90; $i++) {
        $r = Invoke-Hse400 -Method GET -Path $probe
        if ($r.Status -ne 409 -or (Get-EcrErrorCode $r) -ne 'ECR-DOC-4091') { return }
        Start-Sleep -Seconds 2
    }
    throw "Документ $DocumentId після переносу на версію $VersionId досі зайнятий (ECR-DOC-4091)."
}

function Set-Hse400Bindings {
    <# Активні прив'язки {} на якір кожної таблиці версії шаблону. Лише коли методологія має опубліковану версію
       (інакше $false): прив'язка до неопублікованої валить увесь перерахунок документа. Перенос проєкту на нову версію
       шаблону вимагає прив'язок на її якорях ДО переносу (відмова bindingsNotMapped). #>
    param([Parameter(Mandatory)]$MethodologyId, [Parameter(Mandatory)]$VersionId)
    $published = @(Get-Hse400MethodologyVersions $MethodologyId | Where-Object { [string]$_.status -eq 'Published' })
    if (-not $published.Count) { return $false }
    $anchors = Get-Hse400AnchorColumnIds -Structure (Get-Hse400Structure $VersionId) -Model $model
    $existing = Get-Hse400Bindings $MethodologyId
    $made = 0; $kept = 0
    foreach ($b in $plan.Bindings) {
        if (-not $anchors.ContainsKey($b.Table)) { throw "У версії шаблону $VersionId немає колонки $($b.ColumnCode) у таблиці $($b.Table)." }
        $col = $anchors[$b.Table]
        $same = @($existing | Where-Object { [int]$_.columnDefId -eq $col -and [string]$_.outputCode -eq $b.OutputCode -and $_.isActive -and ([string]$_.matchJson).Trim() -eq '{}' })
        if ($same.Count) { $kept++; continue }
        Invoke-Hse400Ok -Method PUT -Path ("/api/v1/methodologies/{0}/bindings/{1}/{2}" -f $MethodologyId, $col, $b.OutputCode) -Body (New-Hse400BindingBody -Active $true) | Out-Null
        $made++
    }
    Write-Hse400Log ("Прив'язки {0}: створено/оновлено {1}, уже були {2} з {3}." -f $plan.Namespace, $made, $kept, $plan.Bindings.Count)
    return $true
}

function Write-Hse400GoldenInputs {
    <# Пише ввід золотих тестів у золотий документ (повтор - ті самі значення поверх наявних рядків). #>
    param([Parameter(Mandatory)]$Gold, [Parameter(Mandatory)]$Tests)
    $writes = Get-Hse400GoldenCellWrites -Tests $Tests
    foreach ($table in $writes.Keys) {
        if (-not $Gold.Instances.ContainsKey($table)) { throw "У золотому документі $($Gold.DocumentId) немає таблиці $table." }
        $ti = $Gold.Instances[$table]
        $slice = (Invoke-Hse400Ok -Method GET -Path ("/api/v1/documents/{0}/tables/{1}?periodKey={2}" -f $Gold.DocumentId, $ti, $Gold.PeriodKey)).Json
        $rv = @{}
        foreach ($r in @($slice.rows)) { if ($null -ne $r) { $rv[[string]$r.rowKey] = [string]$r.rowVersion } }
        $body = New-Hse400CellsPatchBody -PeriodKey $Gold.PeriodKey -TableInstanceId $ti -Rows $writes[$table] -RowVersions $rv
        Invoke-Hse400Ok -Method PATCH -Path ("/api/v1/documents/{0}/cells" -f $Gold.DocumentId) -Body $body | Out-Null
        Write-Hse400Log ("  золотий ввід {0}: рядків {1}." -f $table, @($writes[$table]).Count)
    }
}

Set-Hse400Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly
$model = Read-Hse400Model
$months = @(Get-Hse400MonthColumns)
$plan = Get-Hse400MethodologyPlan $model
$tplCode = [string]$model.template.code
$tplVersion = [string]$model.template.version
$projectCode = '{0}_{1}' -f $tplCode, $Year
$policyCode = 'HSE400_OPEN'
$goldCode = '{0}_GOLD' -f $tplCode
$state = Read-Hse400State
Connect-Hse400
Write-Hse400Log ("Стенд {0}; шаблон {1} {2}; методологія {3} {4}; етап {5}{6}" -f (Get-Hse400StandName), $tplCode, $tplVersion, $plan.Namespace, $plan.Version, $Stage, $(if ($PreviewOnly) { ' (лише перегляд)' } else { '' }))

$units = @(Get-Hse400Items (Invoke-Hse400Ok -Method GET -Path '/api/v1/units').Json)
$unitIds = @{}
foreach ($u in $units) { $unitIds[[string]$u.code] = [int]$u.id }

# ---------- перегляд ----------
if ($PreviewOnly) {
    try { $one = Find-Hse400UnitId $units; Write-Hse400Log "Одиниця 'one' є (id $one)." } catch { Add-Hse400Incomplete $_.Exception.Message }
    $t = Find-Hse400Template $tplCode
    if ($null -eq $t) { Write-Hse400Log "Шаблону $tplCode немає - було б створено разом із версією $tplVersion." }
    else {
        $vs = Get-Hse400Versions (Get-Hse400Id $t 'id', 'templateId')
        $pub = @($vs | Where-Object { [string]$_.version -eq $tplVersion -and [string]$_.status -eq 'Published' })
        $dr = @($vs | Where-Object { [string]$_.version -eq $tplVersion -and [string]$_.status -eq 'Draft' })
        if ($pub.Count) {
            $bad = @(Test-Hse400Structure -Structure (Get-Hse400Structure $pub[0].id) -Model $model)
            if ($bad.Count) { Add-Hse400Incomplete ("Версію $tplVersion опубліковано зі структурою, якої модуль не прийме: " + ($bad -join '; ') + ' - потрібен новий номер версії шаблону.') }
            else { Write-Hse400Log "Версію $tplVersion опубліковано, структура відповідає модулю." }
        }
        elseif ($dr.Count) { Write-Hse400Log "Є чернетка $tplVersion - було б дозаповнено й опубліковано." }
        else { Write-Hse400Log "Версії $tplVersion немає - було б створено чернетку й опубліковано." }
    }
    $rows = 0; foreach ($tb in @($model.tables)) { $rows += @($tb.rows).Count }
    Write-Hse400Log ("Модель: таблиць {0}, рядків {1}, місячних колонок {2} + якір {3}; аргументів {4}, цілей F_ {5}, формул у моделі {6}, тестів {7}." -f @($model.tables).Count, $rows, $months.Count, (Get-Hse400AnchorColumnCode), $plan.Arguments.Count, $plan.Targets.Count, $plan.Formulas.Count, $plan.Tests.Count)
    if (-not $plan.Ready) { Add-Hse400Incomplete ("Модель не готова до публікації методології: без формули {0} з {1} рядків F_, тестів {2}." -f $plan.MissingFormulas.Count, $plan.Targets.Count, $plan.Tests.Count) }
    $m = Find-Hse400Methodology $plan.Namespace
    if ($null -eq $m) { Write-Hse400Log "Методології $($plan.Namespace) немає - було б створено з версією $($plan.Version) рівня Module." }
    else {
        $mv = Get-Hse400MethodologyVersions $m.id | Where-Object { [string]$_.versionNumber -eq $plan.Version } | Select-Object -First 1
        if ($null -eq $mv) { Write-Hse400Log "Методологія $($plan.Namespace) є (id $($m.id)), версії $($plan.Version) немає - було б створено." }
        else {
            Write-Hse400Log ("Методологія {0} (id {1}), версія {2}: {3}, рівень {4}." -f $plan.Namespace, $m.id, $plan.Version, $mv.status, $mv.level)
            if ([string]$mv.level -ne 'Module') { Add-Hse400Incomplete "Версія методології $($plan.Version) рівня $($mv.level), модуль приймає лише Module." }
        }
        $active = @(Get-Hse400Bindings $m.id | Where-Object { $_.isActive })
        Write-Hse400Log ("Активних прив'язок методології: {0} (потрібно {1})." -f $active.Count, $plan.Bindings.Count)
    }
    $p = Get-Hse400AllItems -Path '/api/v1/projects' | Where-Object { [string]$_.code -eq $projectCode } | Select-Object -First 1
    if ($null -eq $p) { Write-Hse400Log "Проєкту $projectCode немає - було б створено з одним документом." }
    else { Write-Hse400Log "Проєкт $projectCode уже є." }
    if ($plan.Tests.Count) {
        $g = Get-Hse400AllItems -Path '/api/v1/projects' | Where-Object { [string]$_.code -eq $goldCode } | Select-Object -First 1
        if ($null -eq $g) { Write-Hse400Log "Золотого проєкту $goldCode немає - було б створено з одним документом і вводом $($plan.Tests.Count) тестів (лише поки методологія в чернетці)." }
        else { Write-Hse400Log "Золотий проєкт $goldCode уже є." }
    }
    foreach ($w in $script:Incomplete) { Write-Warning $w }
    if ($script:Incomplete.Count) { exit 2 }
    return
}

# ---------- шаблон ----------
if ($Stage -in 'Template', 'All') {
    $oneId = Find-Hse400UnitId $units
    Write-Hse400Log "Одиниця 'one' є (id $oneId)."

    $t = Find-Hse400Template $tplCode
    if ($null -eq $t) {
        $r = Invoke-Hse400Ok -Method POST -Path '/api/v1/templates' -Body @{ code = $tplCode; nameL10n = @{ en = [string]$model.template.en; ru = [string]$model.template.ru } }
        $templateId = Get-Hse400Id $r.Json 'templateId', 'id'
        Write-Hse400Log "Створено шаблон $tplCode (id $templateId)."
    }
    else { $templateId = Get-Hse400Id $t 'id', 'templateId' }
    $vs = Get-Hse400Versions $templateId
    $pub = $vs | Where-Object { [string]$_.version -eq $tplVersion -and [string]$_.status -eq 'Published' } | Select-Object -First 1
    if ($null -ne $pub) {
        $vid = Get-Hse400Id $pub 'id'
        $bad = @(Test-Hse400Structure -Structure (Get-Hse400Structure $vid) -Model $model)
        if ($bad.Count) { throw ("Версію $tplVersion (id $vid) опубліковано зі структурою, якої модуль не прийме: " + ($bad -join '; ') + '. Опубліковану версію не змінюю: підніміть template.version у hse400-model.json.') }
        Write-Hse400Log "Версію $tplVersion уже опубліковано (id $vid), структура відповідає модулю."
    }
    else {
        $dr = $vs | Where-Object { [string]$_.version -eq $tplVersion -and [string]$_.status -eq 'Draft' } | Select-Object -First 1
        if ($null -ne $dr) { $vid = Get-Hse400Id $dr 'id' }
        else {
            $r = Invoke-Hse400Ok -Method POST -Path ("/api/v1/templates/{0}/versions" -f $templateId) -Body @{ versionNumber = $tplVersion; cloneFromVersionId = $null }
            $vid = Get-Hse400Id $r.Json 'versionId', 'id'
        }
        Write-Hse400Log "Чернетка $tplVersion (id $vid)."
        $sheet = [string]$model.sheet.code
        Invoke-Hse400Ok -Method PUT -Path ("/api/v1/template-versions/{0}/sheets/{1}" -f $vid, $sheet) -Body @{
            nameL10n = @{ en = [string]$model.sheet.en; ru = [string]$model.sheet.ru }; ordinal = 1; sheetGroup = $null; isMandatory = $true; isVisible = $true
        } | Out-Null
        $tOrd = 0
        foreach ($tb in @($model.tables)) {
            $tOrd++
            $r = Invoke-Hse400Ok -Method PUT -Path ("/api/v1/template-versions/{0}/sheets/{1}/tables/{2}" -f $vid, $sheet, $tb.code) -Body (New-Hse400TableBody -Table $tb -Ordinal $tOrd)
            $tid = Get-Hse400Id $r.Json 'id'
            foreach ($m in $months) {
                Invoke-Hse400Ok -Method PUT -Path ("/api/v1/template-versions/{0}/tables/{1}/columns/{2}" -f $vid, $tid, $m.Code) -Body (New-Hse400MonthColumnBody -Month $m) | Out-Null
            }
            Invoke-Hse400Ok -Method PUT -Path ("/api/v1/template-versions/{0}/tables/{1}/columns/{2}" -f $vid, $tid, (Get-Hse400AnchorColumnCode)) -Body (New-Hse400AnchorColumnBody) | Out-Null
            $rOrd = 0
            foreach ($row in @($tb.rows)) {
                $rOrd++
                Invoke-Hse400Ok -Method PUT -Path ("/api/v1/template-versions/{0}/tables/{1}/rows/{2}" -f $vid, $tid, $row.key) -Body (New-Hse400RowBody -Row $row -Ordinal $rOrd) | Out-Null
            }
            Write-Hse400Log ("  {0}: MonthsInColumns, колонок {1} + якір, рядків {2}." -f $tb.code, $months.Count, $rOrd)
        }
        $bad = @(Test-Hse400Structure -Structure (Get-Hse400Structure $vid) -Model $model)
        if ($bad.Count) { throw ("Чернетка $vid після запису не відповідає модулю: " + ($bad -join '; ') + '. Не публікую.') }
        Invoke-Hse400Ok -Method POST -Path ("/api/v1/template-versions/{0}/publish" -f $vid) -Body @{ reason = "HSE400 $tplVersion" } | Out-Null
        Write-Hse400Log "Версію $tplVersion опубліковано."
    }
    $state['templateId'] = $templateId; $state['templateVersionId'] = $vid
    Save-Hse400State $state

    # ---------- методологія HSE400 (модуль місячних колонок) ----------
    $meth = Find-Hse400Methodology $plan.Namespace
    if ($null -eq $meth) {
        $meth = (Invoke-Hse400Ok -Method POST -Path '/api/v1/methodologies' -Body (New-Hse400MethodologyBody $model)).Json
        Write-Hse400Log "Створено методологію $($plan.Namespace) (id $($meth.id))."
    }
    $mid = Get-Hse400Id $meth 'id'
    $mv = Get-Hse400MethodologyVersions $mid | Where-Object { [string]$_.versionNumber -eq $plan.Version } | Select-Object -First 1
    if ($null -eq $mv) {
        $mv = (Invoke-Hse400Ok -Method POST -Path ("/api/v1/methodologies/{0}/versions" -f $mid) -Body (New-Hse400MethodologyVersionBody $model)).Json
        Write-Hse400Log "Створено чернетку методології $($plan.Version) рівня Module (id $($mv.id))."
    }
    $mvid = Get-Hse400Id $mv 'id'
    if ([string]$mv.level -ne 'Module') { throw "Версія методології $($plan.Namespace) $($plan.Version) (id $mvid) має рівень $($mv.level); модуль місячних колонок приймає лише Module. Рівень клону не змінюється - підніміть methodology.version у hse400-model.json." }
    switch ([string]$mv.status) {
        'Published' { Write-Hse400Log "Версію методології $($plan.Version) уже опубліковано (id $mvid) - не чіпаю." }
        'Draft' {
            $base = "/api/v1/methodologies/{0}/versions/{1}" -f $mid, $mvid
            Invoke-Hse400Ok -Method PUT -Path ("{0}/rules/{1}" -f $base, (Get-Hse400CatchAllRuleCode)) -Body (New-Hse400CatchAllRuleBody) | Out-Null
            foreach ($c in $plan.Constants) { Invoke-Hse400Ok -Method PUT -Path ("{0}/constants/{1}" -f $base, $c.code) -Body (New-Hse400ConstantBody -Constant $c -UnitIds $unitIds) | Out-Null }
            foreach ($f in $plan.Formulas) { Invoke-Hse400Ok -Method PUT -Path ("{0}/formulas/{1}" -f $base, $f.code) -Body (New-Hse400FormulaBody -Formula $f) | Out-Null }
            # Виходи прив'язок і золотих тестів (Get-Hse400MethodologyPlan): після публікації їх уже не оголосити.
            $outOrd = 0
            foreach ($oc in @($plan.Outputs)) {
                $outOrd++
                Invoke-Hse400Ok -Method PUT -Path ("{0}/outputs/{1}" -f $base, $oc) -Body (New-Hse400OutputBody -UnitId $oneId -Ordinal $outOrd) | Out-Null
            }
            if ($plan.Tests.Count) {
                # Модуль читає комірки документа, тож золотий тест потребує документа з відомим вводом. Це окремий
                # проєкт HSE400_GOLD: синтетичний ввід ніколи не потрапляє в робочий документ HSE400_<рік>.
                # Наявний золотий проєкт на старішій версії шаблону переноситься, а перенос вимагає прив'язок на якорях
                # нової версії - їх дає попередня опублікована версія методології (якщо є).
                [void](Set-Hse400Bindings -MethodologyId $mid -VersionId $vid)
                $gold =Get-Hse400ProjectDocument -Code $goldCode -Title 'HSE400 golden test (do not edit)' -ProjectYear $Year -VersionId $vid -PolicyId (Get-Hse400PolicyId $policyCode)
                Write-Hse400GoldenInputs -Gold $gold -Tests $plan.Tests
                foreach ($x in $plan.Tests) {
                    if (-not $gold.Instances.ContainsKey([string]$x.table)) { throw "У золотому документі немає таблиці $($x.table) (тест $($x.code))." }
                    $tb = New-Hse400GoldenTestBody -Test $x -DocumentId $gold.DocumentId -TableInstanceId $gold.Instances[[string]$x.table] -PeriodKey $gold.PeriodKey -Namespace $plan.Namespace
                    Invoke-Hse400Ok -Method PUT -Path ("{0}/tests/{1}" -f $base, $x.code) -Body $tb | Out-Null
                }
                $state['goldProjectId'] = $gold.ProjectId; $state['goldDocumentId'] = $gold.DocumentId
            }
            Write-Hse400Log ("Чернетка методології: правило {0} = {{}}, констант {1}, формул {2}, тестів {3}." -f (Get-Hse400CatchAllRuleCode), $plan.Constants.Count, $plan.Formulas.Count, $plan.Tests.Count)
            if ($plan.Ready) {
                Invoke-Hse400Ok -Method POST -Path ("{0}/publish" -f $base) -Body (New-Hse400MethodologyPublishBody $model) | Out-Null
                Write-Hse400Log "Версію методології $($plan.Version) опубліковано."
            }
            else {
                $sample = @($plan.MissingFormulas | Select-Object -First 3 | ForEach-Object { $_.FormulaCode }) -join ', '
                Add-Hse400Incomplete ("Методологію не опубліковано: у hse400-model.json (methodology.formulas/constants/tests) без формули {0} з {1} рядків F_ (напр. {2}), тестів {3}. Формули не вигадуються - їх має дати генератор моделі з вивантаження AF Methodologies\HSE400\V1." -f $plan.MissingFormulas.Count, $plan.Targets.Count, $sample, $plan.Tests.Count)
            }
        }
        default { throw "Версія методології $($plan.Version) (id $mvid) у стані $($mv.status) - не чіпаю." }
    }
    $state['methodologyId'] = $mid; $state['methodologyVersionId'] = $mvid
    Save-Hse400State $state

    # ---------- прив'язки: якір кожної таблиці -> HSE400, правило "{}" ----------
    if (-not (Set-Hse400Bindings -MethodologyId $mid -VersionId $vid)) {
        Add-Hse400Incomplete "Прив'язки не створено: методологія $($plan.Namespace) без опублікованої версії (активна прив'язка до неї валить увесь перерахунок документа, ECR-CALC-0422)."
    }
}

# ---------- проєкт і документ ----------
if ($Stage -in 'Project', 'All') {
    if (-not $state.ContainsKey('templateVersionId')) { throw 'У стані немає templateVersionId: спершу -Stage Template на цьому стенді.' }
    $vid = $state['templateVersionId']
    $work = Get-Hse400ProjectDocument -Code $projectCode -Title "HSE400 $Year" -ProjectYear $Year -VersionId $vid -PolicyId (Get-Hse400PolicyId $policyCode)
    $state['projectId'] = $work.ProjectId; $state['documentId'] = $work.DocumentId; $state['periodKey'] = $work.PeriodKey; $state['tableInstances'] = $work.Instances
    Save-Hse400State $state
    Write-Hse400Log ("Документ {0}, період {1}, таблиць {2}. Стан: {3}" -f $work.DocumentId, $work.PeriodKey, $work.Instances.Count, (Get-Hse400StateFile))
}

foreach ($w in $script:Incomplete) { Write-Warning $w }
if ($script:Incomplete.Count) { exit 2 }
