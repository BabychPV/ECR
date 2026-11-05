<#
.SYNOPSIS
    HSE401: привести УЖЕ завантажений стенд до поточної моделі шаблону (Hse401Model.Lib.ps1), не перестворюючи об'єктів.
    Build-Hse401Template.ps1 наявне не чіпає, тому зміни моделі (колонка L = H_NAE, назви, формати показу) сюди не дійдуть.

    -PreviewOnly: лише GET, звіт різниці (назви, прибрані колонки, порядок, формати) між опублікованою версією і моделлю.
    Без -PreviewOnly: клон опублікованої версії -> різниця (PATCH presentation: назви, формати, порядок, IsHidden) -> publish ->
    migrate-version документа(ів) проєкту HSE401_2026 (спершу dry-run; будь-яка помилка dry-run зупиняє) -> тиша перерахунку ->
    Compare-Hse401.ps1 (REAL має бути 0). Другий запуск: різниця порожня й документ уже на останній версії -> нічого не робить.

    Колонку, якої в моделі більше немає (H_MPE), за замовчуванням ХОВАЄМО (IsHidden=true): значення лишаються, Safe-перенос не втрачає даних.
    Видалення (DELETE колонки) - лише за -DeleteRemovedColumns, і Safe-перенос відмовить (dataLoss), якщо в колонці є значення; тому до publish
    скрипт перевіряє рядки документів проєкту (GET /documents/{id}/tables/status -> rowCount) і при будь-якому рядку зупиняється ДО publish.

.PARAMETER DeleteRemovedColumns  видаляти прибрані з моделі колонки замість приховування (небезпечно: dataLoss у Safe-переносі, якщо є значення).
.PARAMETER HideInsteadOfDelete   застаріло, нічого не робить (приховування тепер типове); лишено для сумісності командних рядків.
.PARAMETER RebuildState          перебудувати файл стану з API за кодами (шаблон HSE401, проєкт HSE401_2026), якщо його немає або він неповний.
.PARAMETER NewVersion            номер нової версії (Major.Minor.Patch.Build); за замовчуванням остання опублікована + 1 до останнього числа.
.PARAMETER SkipCompare           не запускати Compare наприкінці (не рекомендовано).
#>
[CmdletBinding()]
param(
    [string]$NewVersion,
    [switch]$DeleteRemovedColumns,
    [switch]$HideInsteadOfDelete,
    [switch]$RebuildState,
    [switch]$SkipCompare,
    [ValidateSet('Scratch', 'Live')][string]$Target = 'Scratch', [string]$BaseUrl, [string]$Database, [string]$ConfirmStand, [string]$CredentialFile,
    [switch]$PreviewOnly, [switch]$AcceptNaePreview
)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
. (Join-Path $here 'Hse401Model.Lib.ps1')

# ---------- чисті функції (без HTTP; їх перевіряє Test-Hse401Guards.ps1) ----------

function Get-HseL10nText {
    <# Підпис однієї мови зі структури: { values: { en, ru } } або плоский { en, ru }. #>
    param($L10n, [string]$Lang)
    if ($null -eq $L10n) { return $null }
    $src = if ($L10n.PSObject.Properties['values']) { $L10n.values } else { $L10n }
    if ($null -eq $src) { return $null }
    foreach ($p in $src.PSObject.Properties) { if ($p.Name -ieq $Lang) { return [string]$p.Value } }
    return $null
}

function Get-HseTemplateDiff {
    <# Різниця «опублікована структура (колонки таблиці) -> модель». Повертає список @{ Kind; Code; Field; From; To }.
       Kind: Retitle | DisplayFormat | RemoveColumn | HideColumn | Reorder | MissingColumn (помилка: у версії немає колонки моделі).
    Прибрана з моделі колонка (H_MPE): типово HideColumn (вже схована = виконано); RemoveColumn - лише з -DeleteRemoved. #>
    param([Parameter(Mandatory)]$LiveColumns, [Parameter(Mandatory)]$ModelColumns, [switch]$DeleteRemoved)
    $diff = New-Object System.Collections.Generic.List[object]
    $live = @{}; foreach ($lc in @($LiveColumns)) { $live[[string]$lc.code] = $lc }
    $modelCodes = @($ModelColumns | ForEach-Object { $_.Code })
    foreach ($m in $ModelColumns) {
        if (-not $live.ContainsKey($m.Code)) { $diff.Add([pscustomobject]@{ Kind = 'MissingColumn'; Code = $m.Code; Field = ''; From = $null; To = $null }); continue }
        $l = $live[$m.Code]
        foreach ($lang in 'en', 'ru') {
            $want = if ($lang -eq 'en') { $m.En } else { $m.Ru }
            if (-not $want) { continue }
            $have = Get-HseL10nText $l.headerL10n $lang
            if ($have -cne $want) { $diff.Add([pscustomobject]@{ Kind = 'Retitle'; Code = $m.Code; Field = $lang; From = $have; To = $want }) }
        }
        $wantF = if ($m.DisplayFormat) { [string]$m.DisplayFormat } else { '' }
        $haveF = if ($l.displayFormat) { [string]$l.displayFormat } else { '' }
        if ($haveF -cne $wantF) { $diff.Add([pscustomobject]@{ Kind = 'DisplayFormat'; Code = $m.Code; Field = 'displayFormat'; From = $haveF; To = $wantF }) }
    }
    # колонки, яких у моделі більше немає (H_MPE): видалити (чернетка, м'яко) або сховати
    foreach ($code in $live.Keys) {
        if ($modelCodes -contains $code) { continue }
        if ($code -ne 'H_MPE') { continue }   # чужі/ручні колонки не чіпаємо
        if ($DeleteRemoved) { $diff.Add([pscustomobject]@{ Kind = 'RemoveColumn'; Code = $code; Field = ''; From = 'present'; To = 'removed' }) }
        elseif (-not [bool]$live[$code].isHidden) { $diff.Add([pscustomobject]@{ Kind = 'HideColumn'; Code = $code; Field = 'isHidden'; From = 'false'; To = 'true' }) }
    }
    # порядок: відносний порядок колонок моделі, що є у версії (прибрані/сховані колонки не рахуємо)
    $liveOrder = @($live.Values | Where-Object { $modelCodes -contains [string]$_.code } | Sort-Object { [int]$_.ordinal } | ForEach-Object { [string]$_.code })
    $modelOrder = @($modelCodes | Where-Object { $live.ContainsKey($_) })
    if (($liveOrder -join ',') -ne ($modelOrder -join ',')) { $diff.Add([pscustomobject]@{ Kind = 'Reorder'; Code = '*'; Field = 'ordinal'; From = ($liveOrder -join ','); To = ($modelOrder -join ',') }) }
    return $diff.ToArray()   # викликати як @(Get-HseTemplateDiff ...)
}

function Get-HseNextVersion {
    <# '1.0.0.2' -> '1.0.0.3'; пропускає вже наявні номери. #>
    param([string]$Published, [string[]]$Existing = @())
    $parts = $Published.Split('.')
    $n = [int]$parts[$parts.Length - 1]
    do { $n++; $parts[$parts.Length - 1] = [string]$n; $cand = $parts -join '.' } while ($Existing -contains $cand)
    return $cand
}

function Test-HseDocOnVersion {
    <# Документ уже на версії? GET /documents/{id} віддає лише рядок templateVersion ("1.0.0.3"), без id версії: порівнюємо з рядком version
       останньої опублікованої версії. Порожній/невідомий рядок = НЕ на версії (перенос спробується; 422 migrateSameVersion далі трактується як "вже зроблено"). #>
    param([AllowNull()][string]$DocTemplateVersion, [AllowNull()][string]$PublishedVersion)
    if ([string]::IsNullOrWhiteSpace($DocTemplateVersion) -or [string]::IsNullOrWhiteSpace($PublishedVersion)) { return $false }
    return ($DocTemplateVersion.Trim() -ceq $PublishedVersion.Trim())
}

function Test-HseMigrateSameVersion {
    <# Відповідь migrate-version = 422 ECR-TMPL-0422 migrateSameVersion (документ уже на цільовій версії) - це "вже зроблено", не помилка. #>
    param($Response)
    if ($null -eq $Response -or [int]$Response.Status -ne 422) { return $false }
    return ([string]$Response.Text -match 'migrateSameVersion')
}

if ($MyInvocation.InvocationName -eq '.') { return }   # підключення як бібліотеки (guard-тест)

# ---------- запуск ----------

Set-Hse401Target -Target $Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly -AcceptNaePreview:$AcceptNaePreview
$out = Get-Hse401OutDir
$stateFile = Get-Hse401StateFile
$preview = Test-Hse401Preview
Connect-Hse
$stateKeys = 'templateId', 'versionId', 'sheetDefId', 'tableDefId', 'columnIds', 'projectId', 'documentId', 'tableInstanceId'
$state = if (Test-Path $stateFile) { Read-HseJson $stateFile } else { @{} }
Assert-Hse401StateMatches $state    # D4: стан іншого стенда - відмова до першого запису
$stateMissing = @($stateKeys | Where-Object { -not $state.ContainsKey($_) -or $null -eq $state[$_] })
if ($stateMissing.Count -gt 0) {
    if (-not $RebuildState) { throw "Файл стану $stateFile відсутній або неповний (немає: $($stateMissing -join ', ')). Запустіть з -RebuildState (перебудова з API за кодами) або спершу Build/Load." }
    Write-HseLog "== RebuildState: стан збирається з API (шаблон $($script:HseTemplateCode), проєкт HSE401_2026) =="
    $tq = (Invoke-HseOk GET ("/api/v1/templates?limit=50&q=" + $script:HseTemplateCode)).Json
    $tplR = @($tq.items) | Where-Object { $_.code -eq $script:HseTemplateCode } | Select-Object -First 1
    if ($null -eq $tplR) { throw "Шаблону $($script:HseTemplateCode) на стенді немає." }
    $versR = @((Invoke-HseOk GET "/api/v1/templates/$($tplR.id)/versions?limit=100").Json.items)
    $pubR = $versR | Where-Object { $_.status -eq 'Published' } | Sort-Object { [int]$_.id } -Descending | Select-Object -First 1
    if ($null -eq $pubR) { throw 'Опублікованої версії шаблону немає.' }
    $stR = (Invoke-HseOk GET "/api/v1/template-versions/$($pubR.id)/structure").Json
    $sheetR = @($stR.sheets) | Where-Object { $_.code -eq $script:HseSheetCode } | Select-Object -First 1
    $tableR = @($sheetR.tables) | Where-Object { $_.code -eq $script:HseTableCode } | Select-Object -First 1
    if ($null -eq $tableR) { throw "У версії $($pubR.version) немає таблиці $($script:HseTableCode)." }
    $cidR = @{}; foreach ($c in @($tableR.columns)) { $cidR[[string]$c.code] = [int]$c.id }
    $foundR = Find-Hse401Document
    if ($null -eq $foundR -or $null -eq $foundR['documentId']) { throw 'Проєкт HSE401_2026 або його документ не знайдено - перебудувати стан неможливо.' }
    foreach ($kv in @{ templateId = [int]$tplR.id; versionId = [int]$pubR.id; sheetDefId = [int]$sheetR.id; tableDefId = [int]$tableR.id; columnIds = $cidR
            projectId = [int]$foundR['projectId']; documentId = [int]$foundR['documentId']; periodKey = $foundR['periodKey']; tableInstanceId = $foundR['tableInstanceId'] }.GetEnumerator()) { $state[$kv.Key] = $kv.Value }
    $state['standKey'] = Get-Hse401StandKey
    if ($preview) { Write-HseLog '[preview] стан зібрано в пам''яті, файл не записується.' }
    else { [IO.File]::WriteAllText($stateFile, (ConvertTo-HseJson -Value $state).ToString(), (New-Object Text.UTF8Encoding($false))); Write-HseLog "  стан записано: $stateFile" }
}
$model = Get-Hse401Columns   # без @(): функція повертає масив через ", " - @() загорнув би його ще раз

# 1. остання опублікована версія шаблону
Write-HseLog '== 1. Поточна опублікована версія =='
$tplId = [int]$state['templateId']
$vers = @((Invoke-HseOk GET "/api/v1/templates/$tplId/versions?limit=100").Json.items)
$pub = $vers | Where-Object { $_.status -eq 'Published' } | Sort-Object { [int]$_.id } -Descending | Select-Object -First 1
if ($null -eq $pub) { throw 'Опублікованої версії шаблону немає.' }
$draftOpen = $vers | Where-Object { $_.status -eq 'Draft' } | Select-Object -First 1
if ($null -ne $draftOpen) { throw "Є незавершена чернетка $($draftOpen.version) (id $($draftOpen.id)); не чіпаю - розберіться з нею вручну." }
$pubId = [int]$pub.id
Write-HseLog "  версія $($pub.version) (id $pubId)"
function Get-TableOf($vid) {
    $st = (Invoke-HseOk GET "/api/v1/template-versions/$vid/structure").Json
    $sheet = @($st.sheets) | Where-Object { $_.code -eq $script:HseSheetCode } | Select-Object -First 1
    $table = @($sheet.tables) | Where-Object { $_.code -eq $script:HseTableCode } | Select-Object -First 1
    if ($null -eq $table) { throw "У версії $vid немає таблиці $($script:HseTableCode)." }
    return $table
}
$liveTable = Get-TableOf $pubId
$diff = @(Get-HseTemplateDiff -LiveColumns $liveTable.columns -ModelColumns $model -DeleteRemoved:$DeleteRemovedColumns)
if ($HideInsteadOfDelete) { Write-HseLog '  -HideInsteadOfDelete застаріло: приховування тепер типове, ключ нічого не змінює.' }

# 2. документи проєкту і чи всі вже на останній версії (GET /documents/{id} віддає лише рядок templateVersion, напр. "1.0.0.3")
$found = Find-Hse401Document
$docIds = @()
if ($null -ne $found -and $null -ne $found['documentId']) {
    $docs = Get-HseItems (Invoke-HseOk GET "/api/v1/documents?limit=200&projectId=$($found['projectId'])").Json
    $docIds = @($docs | Where-Object { $_.projectId -eq $found['projectId'] } | ForEach-Object { [int]$_.id })
}
function Get-DocTemplateVersion($docId) {
    $d = (Invoke-HseOk GET "/api/v1/documents/$docId").Json
    if ($d.PSObject.Properties['templateVersion']) { return [string]$d.templateVersion }
    return ''
}
$pendingDocs = @()
foreach ($d in $docIds) { if (-not (Test-HseDocOnVersion -DocTemplateVersion (Get-DocTemplateVersion $d) -PublishedVersion ([string]$pub.version))) { $pendingDocs += $d } }

# 2b. -DeleteRemovedColumns: видалення колонки з даними блокує Safe-перенос (dataLoss), а опублікована версія незворотна -
# тому рядки документів перевіряємо ДО clone/publish. GET /documents/{id}/tables/status?periodKey= -> [{ tableDefId, rowCount, ... }].
$removeCols = @($diff | Where-Object { $_.Kind -eq 'RemoveColumn' })
if ($removeCols.Count -gt 0) {
    Write-HseLog ("УВАГА: -DeleteRemovedColumns: колонки {0} буде ВИДАЛЕНО. Safe-перенос відмовить (dataLoss), якщо в колонці є значення; за замовчуванням їх лише ховають." -f (($removeCols | ForEach-Object { $_.Code }) -join ', '))
    $rowsFound = 0
    if ($docIds.Count -gt 0) {
        $per = (Invoke-HseOk GET "/api/v1/projects/$($found['projectId'])/periods").Json
        foreach ($d in $docIds) {
            foreach ($pk in @(@($per.periods) | ForEach-Object { [int]$_.periodKey })) {
                foreach ($ts in @(Get-HseItems (Invoke-HseOk GET "/api/v1/documents/$d/tables/status?periodKey=$pk").Json)) { $rowsFound += [int]$ts.rowCount }
            }
        }
    }
    Write-HseLog "  рядків у таблицях документів проєкту: $rowsFound"
    if ($rowsFound -gt 0) { throw "Видалення колонки ($(($removeCols | ForEach-Object { $_.Code }) -join ', ')) відхилено ДО publish: у документах проєкту є рядки ($rowsFound), Safe-перенос відмовить через dataLoss, а опублікована версія незворотна. Запустіть без -DeleteRemovedColumns (колонку буде сховано, значення збережено)." }
}

Write-HseLog ("== Різниця версія {0} -> модель: {1} пунктів; документів на старішій версії: {2} ==" -f $pub.version, $diff.Count, $pendingDocs.Count)
foreach ($x in $diff) { Write-HseLog ("  {0,-13} {1,-12} {2,-12} [{3}] -> [{4}]" -f $x.Kind, $x.Code, $x.Field, $x.From, $x.To) }
$missing = @($diff | Where-Object { $_.Kind -eq 'MissingColumn' })
if ($missing.Count -gt 0) { throw ("У версії немає колонок моделі: " + (($missing | ForEach-Object { $_.Code }) -join ', ') + '. Скрипт їх не створює.') }
if ($preview) { Write-HseLog '[preview] запису не було.'; return }

if ($diff.Count -eq 0 -and $pendingDocs.Count -eq 0) { Write-HseLog 'Різниця порожня, документи на останній версії: нічого робити.'; return }

$since = (Get-Date).ToUniversalTime().AddSeconds(-1)
$targetVid = $pubId
if ($diff.Count -gt 0) {
    # 3. клон -> зміни -> publish (будь-яка помилка до publish зупиняє скрипт; чернетка лишається для ручного розбору)
    if (-not $NewVersion) { $NewVersion = Get-HseNextVersion -Published ([string]$pub.version) -Existing @($vers | ForEach-Object { [string]$_.version }) }
    Write-HseLog "== 3. Клон $($pub.version) -> $NewVersion =="
    $cl = Invoke-HseOk POST "/api/v1/template-versions/$pubId/clone" @{ newVersion = $NewVersion }
    $newVid = [int]$cl.Json.versionId
    Write-HseLog "  чернетка id $newVid"
    $tbl = Get-TableOf $newVid
    $byCode = @{}; foreach ($c in @($tbl.columns)) { $byCode[[string]$c.code] = $c }
    $changes = New-Object System.Collections.Generic.List[object]
    $titles = @{}   # code -> ordered en/ru до злиття з наявними мовами
    foreach ($x in $diff) {
        switch ($x.Kind) {
            'Retitle' { if (-not $titles.ContainsKey($x.Code)) { $titles[$x.Code] = @{} }; $titles[$x.Code][$x.Field] = $x.To }
            'DisplayFormat' { $changes.Add(@{ entityType = 'ColumnDef'; entityId = [int]$byCode[$x.Code].id; field = 'DisplayFormat'; value = $(if ($x.To) { [string]$x.To } else { $null }) }) }
            'HideColumn' { $changes.Add(@{ entityType = 'ColumnDef'; entityId = [int]$byCode[$x.Code].id; field = 'IsHidden'; value = 'true' }) }
            'Reorder' {
                $i = 0
                foreach ($m in $model) { $i++; if ($byCode.ContainsKey($m.Code)) { $changes.Add(@{ entityType = 'ColumnDef'; entityId = [int]$byCode[$m.Code].id; field = 'Ordinal'; value = [string]$i }) } }
            }
        }
    }
    foreach ($code in $titles.Keys) {
        $merged = [ordered]@{}   # наявні мови (kk тощо) зберігаємо, en/ru перекриваємо
        $cur = $byCode[$code].headerL10n
        $src = if ($null -ne $cur -and $cur.PSObject.Properties['values']) { $cur.values } else { $cur }
        if ($null -ne $src) { foreach ($p in $src.PSObject.Properties) { $merged[$p.Name] = [string]$p.Value } }
        foreach ($k in $titles[$code].Keys) { $merged[$k] = [string]$titles[$code][$k] }
        $changes.Add(@{ entityType = 'ColumnDef'; entityId = [int]$byCode[$code].id; field = 'HeaderL10n'; value = (ConvertTo-Json -InputObject $merged -Compress) })
    }
    if ($changes.Count -gt 0) {
        $null = Invoke-HseOk PATCH "/api/v1/template-versions/$newVid/presentation" ([object[]]$changes.ToArray())
        Write-HseLog "  PATCH presentation: $($changes.Count) змін (назви, формати, видимість, порядок)"
    }
    foreach ($x in @($diff | Where-Object { $_.Kind -eq 'RemoveColumn' })) {
        $r = Invoke-Hse DELETE "/api/v1/template-versions/$newVid/tables/$($tbl.id)/columns/$($x.Code)"
        if ($r.Status -notin 200, 204) { throw "DELETE колонки $($x.Code) -> HTTP $($r.Status) $(Get-EcrErrorCode $r) $($r.Text). Чернетка id $newVid лишилась (видаліть її в UI); повторіть без -DeleteRemovedColumns - колонку буде сховано." }
        Write-HseLog "  колонку $($x.Code) видалено (м'яко) у чернетці"
    }
    # перевірка чернетки ДО publish: структура = модель
    $left = @(Get-HseTemplateDiff -LiveColumns (Get-TableOf $newVid).columns -ModelColumns $model -DeleteRemoved:$DeleteRemovedColumns)
    if ($left.Count -gt 0) { throw ("Після змін чернетка id $newVid ще відрізняється від моделі: " + (($left | ForEach-Object { "$($_.Kind) $($_.Code)" }) -join '; ') + '. Publish не виконано.') }
    $p = Invoke-Hse POST "/api/v1/template-versions/$newVid/publish" @{ reason = "HSE401: колонка L = вузол NAE (MPE number), назви, формати показу" }
    if ($p.Status -notin 200, 201, 202, 204) { throw "publish -> $($p.Status) $(Get-EcrErrorCode $p) $($p.Text)" }
    Write-HseLog "  опубліковано версію $NewVersion (id $newVid)"
    $targetVid = $newVid
    $state['versionId'] = $newVid
    $cid = @{}; foreach ($c in @((Get-TableOf $newVid).columns)) { $cid[[string]$c.code] = [int]$c.id }
    $state['columnIds'] = $cid; $state['tableDefId'] = [int]$tbl.id
    $state['templateUpdate'] = [ordered]@{ fromVersionId = $pubId; toVersionId = $newVid; version = $NewVersion; at = (Get-Date).ToUniversalTime().ToString('o') }
    $state['standKey'] = Get-Hse401StandKey
    [IO.File]::WriteAllText($stateFile, (ConvertTo-HseJson -Value $state).ToString(), (New-Object Text.UTF8Encoding($false)))
    $pendingDocs = $docIds
}

# 4. перенос документів: dry-run для всіх, і лише тоді застосування
Write-HseLog "== 4. migrate-version -> id $targetVid, документів: $($pendingDocs.Count) =="
$alreadyDone = @{}
foreach ($d in $pendingDocs) {
    $dry = Invoke-Hse POST "/api/v1/documents/$d/migrate-version" @{ targetVersionId = $targetVid; mode = 'Safe'; dryRun = $true }
    if (Test-HseMigrateSameVersion $dry) { Write-HseLog "  документ ${d}: уже на цільовій версії (422 migrateSameVersion) - пропускаю"; $alreadyDone[$d] = $true; continue }
    if ($dry.Status -ne 200 -or -not [bool]$dry.Json.canApply) {
        $t = [string]$dry.Text
        throw ("dry-run документа {0} не пройшов (HTTP {1}, canApply={2}): {3}. Нічого не перенесено. Версія {4} уже опублікована; якщо причина - dataLoss через видалену колонку з даними, її не відновити видаленням: колонки типово ховають (значення збережено)." -f $d, $dry.Status, $(if ($dry.Status -eq 200) { $dry.Json.canApply } else { '?' }), $t.Substring(0, [Math]::Min(900, $t.Length)), $targetVid)
    }
    Write-HseLog "  dry-run документа ${d}: canApply=true"
}
foreach ($d in $pendingDocs) {
    if ($alreadyDone.ContainsKey($d)) { continue }
    $mig = Invoke-Hse POST "/api/v1/documents/$d/migrate-version" @{ targetVersionId = $targetVid; mode = 'Safe'; dryRun = $false; async = $true }
    if (Test-HseMigrateSameVersion $mig) { Write-HseLog "  документ ${d}: уже на цільовій версії (422 migrateSameVersion) - пропускаю"; continue }
    if ($mig.Status -notin 200, 202) { throw "migrate документа $d -> $($mig.Status) $(Get-EcrErrorCode $mig) $($mig.Text)" }
    if ($mig.Status -eq 202) { $job = Wait-HseJob -JobId ([string]$mig.Json.jobId) -TimeoutMinutes 60 -PollSeconds 2; Write-HseLog "  migrate-задача документа ${d}: $($job.state) $($job.message)" }
    $w = Wait-HseDocJobs -DocumentId $d -Since $since -PollSeconds 3
    Write-HseLog ("  документ {0}: перерахунок до тиші, задач {1}, невдалих {2}" -f $d, $w.Jobs, $w.Failed)
    if ($w.Failed -gt 0) { throw "Документ ${d}: $($w.Failed) невдалих задач перерахунку: $($w.Messages -join ' | ')" }
}

# 5. Compare: REAL = 0
if ($SkipCompare) { Write-HseLog '-SkipCompare: порівняння пропущено.'; return }
Write-HseLog '== 5. Compare-Hse401 =='
$cmpArgs = @{ OutPrefix = 'hse401-compare-update'; Target = $Target }
foreach ($k in 'BaseUrl', 'Database', 'ConfirmStand', 'CredentialFile') { $v = Get-Variable -Name $k -ValueOnly; if ($v) { $cmpArgs[$k] = $v } }
& (Join-Path $here 'Compare-Hse401.ps1') @cmpArgs
$realCsv = Join-Path $out 'hse401-compare-update-real.csv'
$real = if (Test-Path $realCsv) { @(Import-Csv -LiteralPath $realCsv -Encoding UTF8).Count } else { -1 }
Write-HseLog "REAL = $real"
if ($real -ne 0) { Write-HseLog "ПОМИЛКА: REAL має бути 0 (файл $realCsv)"; exit 3 }
Write-HseLog 'Готово: шаблон оновлено, документи перенесено, REAL = 0.'
