<#
.SYNOPSIS
    HSE401: привести УЖЕ завантажений стенд до поточної моделі шаблону (Hse401Model.Lib.ps1), не перестворюючи об'єктів.
    Build-Hse401Template.ps1 наявне не чіпає, тому зміни моделі (колонка L = H_NAE, назви, формати показу) сюди не дійдуть.

    -PreviewOnly: лише GET, звіт різниці (назви, прибрані колонки, порядок, формати) між опублікованою версією і моделлю.
    Без -PreviewOnly: клон опублікованої версії -> різниця (PATCH presentation + DELETE колонки) -> publish ->
    migrate-version документа(ів) проєкту HSE401_2026 (спершу dry-run; будь-яка помилка dry-run зупиняє) -> тиша перерахунку ->
    Compare-Hse401.ps1 (REAL має бути 0). Другий запуск: різниця порожня й документ уже на останній версії -> нічого не робить.

.PARAMETER HideInsteadOfDelete  замість видалення H_MPE лише сховати її (якщо видалення колонки з даними блокує перенос).
.PARAMETER NewVersion           номер нової версії (Major.Minor.Patch.Build); за замовчуванням остання опублікована + 1 до останнього числа.
.PARAMETER SkipCompare          не запускати Compare наприкінці (не рекомендовано).
#>
[CmdletBinding()]
param(
    [string]$NewVersion,
    [switch]$HideInsteadOfDelete,
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
       Kind: Retitle | DisplayFormat | RemoveColumn | HideColumn | Reorder | MissingColumn (помилка: у версії немає колонки моделі). #>
    param([Parameter(Mandatory)]$LiveColumns, [Parameter(Mandatory)]$ModelColumns, [switch]$HideInsteadOfDelete)
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
        if ($HideInsteadOfDelete) {
            if (-not [bool]$live[$code].isHidden) { $diff.Add([pscustomobject]@{ Kind = 'HideColumn'; Code = $code; Field = 'isHidden'; From = 'false'; To = 'true' }) }
        }
        else { $diff.Add([pscustomobject]@{ Kind = 'RemoveColumn'; Code = $code; Field = ''; From = 'present'; To = 'removed' }) }
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

if ($MyInvocation.InvocationName -eq '.') { return }   # підключення як бібліотеки (guard-тест)

# ---------- запуск ----------

Set-Hse401Target -Target $Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly -AcceptNaePreview:$AcceptNaePreview
$out = Get-Hse401OutDir
$stateFile = Get-Hse401StateFile
if (-not (Test-Path $stateFile)) { throw "Немає файлу стану $stateFile - спершу Build/Load." }
$state = Read-HseJson $stateFile
Assert-Hse401StateMatches $state    # D4: стан іншого стенда - відмова до першого запису
Connect-Hse
$preview = Test-Hse401Preview
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
$diff = @(Get-HseTemplateDiff -LiveColumns $liveTable.columns -ModelColumns $model -HideInsteadOfDelete:$HideInsteadOfDelete)

# 2. документи проєкту і чи всі вже на останній версії
$found = Find-Hse401Document
$docIds = @()
if ($null -ne $found -and $null -ne $found['documentId']) {
    $docs = Get-HseItems (Invoke-HseOk GET "/api/v1/documents?limit=200&projectId=$($found['projectId'])").Json
    $docIds = @($docs | Where-Object { $_.projectId -eq $found['projectId'] } | ForEach-Object { [int]$_.id })
}
function Get-DocVersion($docId) {
    $d = (Invoke-HseOk GET "/api/v1/documents/$docId").Json
    foreach ($n in 'templateVersionId', 'versionId') { if ($d.PSObject.Properties[$n]) { return [int]$d.$n } }
    return -1
}
$pendingDocs = @()
foreach ($d in $docIds) { $dv = Get-DocVersion $d; if ($dv -ne $pubId) { $pendingDocs += $d } }

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
        if ($r.Status -notin 200, 204) { throw "DELETE колонки $($x.Code) -> HTTP $($r.Status) $(Get-EcrErrorCode $r) $($r.Text). Чернетка id $newVid лишилась; повторіть із -HideInsteadOfDelete (потрібна нова чернетка: видаліть цю в UI)." }
        Write-HseLog "  колонку $($x.Code) видалено (м'яко) у чернетці"
    }
    # перевірка чернетки ДО publish: структура = модель
    $left = @(Get-HseTemplateDiff -LiveColumns (Get-TableOf $newVid).columns -ModelColumns $model -HideInsteadOfDelete:$HideInsteadOfDelete)
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
foreach ($d in $pendingDocs) {
    $dry = Invoke-Hse POST "/api/v1/documents/$d/migrate-version" @{ targetVersionId = $targetVid; mode = 'Safe'; dryRun = $true }
    if ($dry.Status -ne 200 -or -not [bool]$dry.Json.canApply) {
        $t = [string]$dry.Text
        throw ("dry-run документа {0} не пройшов (HTTP {1}, canApply={2}): {3}. Нічого не перенесено. Версія {4} уже опублікована; якщо причина - видалена H_MPE з даними, зніміть блок рішенням і повторіть." -f $d, $dry.Status, $(if ($dry.Status -eq 200) { $dry.Json.canApply } else { '?' }), $t.Substring(0, [Math]::Min(900, $t.Length)), $targetVid)
    }
    Write-HseLog "  dry-run документа ${d}: canApply=true"
}
foreach ($d in $pendingDocs) {
    $mig = Invoke-Hse POST "/api/v1/documents/$d/migrate-version" @{ targetVersionId = $targetVid; mode = 'Safe'; dryRun = $false; async = $true }
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
