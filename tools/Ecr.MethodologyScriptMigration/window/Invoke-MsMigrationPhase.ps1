<#
.SYNOPSIS
    ✎ D-571: фаза вікна «перенесення наявних методологій на ECR Script» (MS, етап 8) — convert → apply → parity → switch → verify,
    і відкат (switch назад на Formulas). Ідемпотентна, кожен крок має ворота; будь-яка розбіжність паритету — нічого не перемикається.
.DESCRIPTION
    Стадії (виконувати по черзі; кожна читає результат попередньої з теки -Work):
      Convert   — інструмент convert на базі (лише читання) → накладання одиниць/джерел Land (Apply-LandScriptUnits) → pack.
                  Ворота: package.json записано (pack звіряє відбиток джерела з convert); невдалі версії — у convert-report.json.
      Apply     — API POST script-migration/apply?dryRun=true; ворота: КОЖЕН запис wouldApply; далі apply; ворота: усі applied.
                  Пише лише тіньовий текст (рушій не змінюється) і подію журналу aud.PublicationEvent на кожну версію.
      Parity    — інструмент parity на базі (лише читання) з --eligible-out. Ворота: 0 розбіжностей (будь-яка → СТОП, eligible.json
                  не існує); у перелік потрапляють лише версії з ≥ 1 порівняним значенням (D-571).
      Switch    — звіряє в базі, що ScriptHash кожної версії переліку = хешу, на якому доведено паритет, і рушій ще Formulas; далі ОДИН
                  атомарний POST script-migration/switch (Script). Відмова 422 (золотий набір PSS-23, склад, цикл) = нічого не змінено.
      Verify    — у базі: рушій Script рівно в переліку; D-568 записано (calc.MethodologyReadField, рядок-маркер) на кожну; вибірковий
                  POST …/script/parity (до -Sample версій) на живих документах.
      ✎ P-1123-4 (людина 23.11):
      Reference — входи з еталонних даних старої системи (ECR, лише читання; Build-MsReferenceCases.ps1) -> parity-cases: паритет
                  формули ↔ скрипт на рядках ECR; ref-eligible.json (хеш тексту) і golden-proposals.json. Розбіжність — СТОП.
      GoldenVersions — для чинних версій із золотим набором без очікувань: нова версія-клон, випадки — вхід і очікування з
                  рядка ECR, публікація (дата чинності «чинна + 1 день»). ПЕРЕД Convert (нові версії стають чинними).
      Switch    — план Get-MsSwitchPlan: доказ live -> еталон ECR -> золотий набір (PSS-23 на Script); вода — ніколи; окремий
                  атомарний switch на кожну версію (причина журналу називає джерело доказу); відмова воріт однієї — BLOCKED у
                  журналі фази, версія лишається на Formulas, інші перемикаються.
      Rollback  — POST switch Formulas на версії з switch-result.json; ворота: рушій Formulas у кожної, таблиці читання зняті.
    Усе, що зроблено, — у теці -Work (phase-log.ndjson, *.json відповідей) і в журналі застосунку (aud.PublicationEvent).
.PARAMETER Stage           GoldenVersions | Convert | Apply | Parity | Reference | Switch | Verify | Rollback.
.PARAMETER ReferenceDatabase  Reference: база старої системи з еталонними даними (ECR; лише читання).
.PARAMETER ReviewerCredentialFile  GoldenVersions на live: акаунт, що публікує нові версії (D-40, друга особа); без нього — той самий
                           акаунт (D-440 дозволяє автору публікувати).
.PARAMETER Work            Тека фази поза репозиторієм (наприклад H:\ECR\.sync-local\w15z-ms).
.PARAMETER Database        База (EcrLandUi на live; EcrLandMs на репетиції).
.PARAMETER BaseUrl         Адреса API (Apply/Switch/Verify/Rollback).
.PARAMETER CredentialFile  Файл «користувач<TAB>пароль» (поза репозиторієм) акаунта з правом Calculation.Publish.
.PARAMETER ToolDll         Зібраний Ecr.MethodologyScriptMigration.dll.
.PARAMETER Reason          Причина перемикання/відкату (журнал).
.PARAMETER Sample          Скільки версій перевірити паритетом через API у Verify.
.PARAMETER CopyAsUser      РЕПЕТИЦІЯ на копії: замість API — команди інструмента apply/switch (ті самі обробники) від імені користувача
                           копії з цим Id; інструмент сам відмовляє на базі, що не схожа на копію.
.PARAMETER Exclude         Apply: версії «КОД ВЕРСІЯ», які свідомо лишаються на Formulas (з причиною у звіті вікна) і не йдуть у пакет;
                           будь-яка ІНША відмова сухого прогону зупиняє фазу.
#>
param(
    [Parameter(Mandatory)][ValidateSet('GoldenVersions', 'Convert', 'Apply', 'Parity', 'Reference', 'Switch', 'Verify', 'Rollback')][string]$Stage,
    [Parameter(Mandatory)][string]$Work,
    [string]$Server = 'localhost',
    [Parameter(Mandatory)][string]$Database,
    [string]$BaseUrl,
    [string]$CredentialFile,
    [string]$ToolDll,
    [string]$Reason = 'W15z: methodologies moved to ECR Script (D-552, D-571); parity proven on live data',
    [int]$Sample = 5,
    [string[]]$Exclude = @(),
    [int]$CopyAsUser = 0,
    [string]$ReferenceDatabase = 'ECR',
    [string]$ReviewerCredentialFile
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
# powershell -File передає «a,b» одним рядком: розбити за комами.
$Exclude = @($Exclude | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
. (Join-Path $PSScriptRoot 'MsPhase.Lib.ps1')
if (-not $ToolDll) { $ToolDll = Join-Path $PSScriptRoot '..\bin\Debug\net10.0\Ecr.MethodologyScriptMigration.dll' }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$cs = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True"
$log = Join-Path $Work 'phase-log.ndjson'

function Write-Phase([string]$what, $data) {
    $line = [ordered]@{ at = (Get-Date).ToUniversalTime().ToString('o'); stage = $Stage; database = $Database; what = $what; data = $data } | ConvertTo-Json -Compress -Depth 8
    [IO.File]::AppendAllText($log, $line + "`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "[$Stage] $what"
}
function Stop-Phase([string]$why, $data) { Write-Phase "STOP: $why" $data; throw "MS phase $Stage stopped: $why" }
function Invoke-Tool([string[]]$toolArgs) {
    if (-not (Test-Path -LiteralPath $ToolDll)) { Stop-Phase "tool not built: $ToolDll" $null }
    & dotnet $ToolDll @toolArgs | ForEach-Object { Write-Host "  $_" }
    return $LASTEXITCODE
}
function Invoke-Sql([string]$query) {
    $rows = & sqlcmd -S $Server -E -d $Database -b -h -1 -W -s '|' -Q ("SET NOCOUNT ON; " + ($query -replace '\s+', ' '))
    if ($LASTEXITCODE -ne 0) { Stop-Phase "sqlcmd failed: $query" $rows }
    return @($rows | Where-Object { $_ -and $_.Trim() })
}
function Read-Json([string]$name) {
    $p = Join-Path $Work $name
    if (-not (Test-Path -LiteralPath $p)) { Stop-Phase "missing $name (run the previous stage)" $null }
    return Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json
}
$script:session = $null
function Connect-Api([string]$file = $CredentialFile) {
    if (-not $BaseUrl -or -not $file) { Stop-Phase 'BaseUrl and CredentialFile are required for this stage' $null }
    $pair = (Get-Content -LiteralPath $file -Raw -Encoding UTF8).Trim() -split "`t", 2
    if ($pair.Count -ne 2) { Stop-Phase 'credential file must be "user<TAB>password"' $null }
    $script:session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $r = Invoke-Api POST '/api/v1/login/local' (@{ userName = $pair[0]; password = $pair[1] } | ConvertTo-Json)
    if ($r.StatusCode -ne 200) { Stop-Phase "login failed ($($r.StatusCode))" $null }
    Write-Phase "login ok as $($pair[0])" $null
}
function Invoke-Api([string]$method, [string]$path, [string]$body) {
    $a = @{ Uri = "$BaseUrl$path"; Method = $method; WebSession = $script:session; UseBasicParsing = $true; ContentType = 'application/json; charset=utf-8'; TimeoutSec = 1800 }
    if ($body) { $a.Body = [Text.Encoding]::UTF8.GetBytes($body) }
    try { $r = Invoke-WebRequest @a; return [pscustomobject]@{ StatusCode = [int]$r.StatusCode; Content = $r.Content } }
    catch {
        $resp = $_.Exception.Response; $t = ''
        if ($resp) { $sr = New-Object IO.StreamReader($resp.GetResponseStream()); $t = $sr.ReadToEnd(); return [pscustomobject]@{ StatusCode = [int]$resp.StatusCode; Content = $t } }
        throw
    }
}
function Get-EngineState([int[]]$ids) {
    if ($ids.Count -eq 0) { return @() }
    return Invoke-Sql ("SELECT v.Id, v.Engine, LOWER(CONVERT(varchar(64), v.ScriptHash, 2)) FROM calc.MethodologyVersion v WHERE v.Id IN (" + ($ids -join ',') + ") ORDER BY v.Id")
}

$sw = [Diagnostics.Stopwatch]::StartNew()
switch ($Stage) {
    'Convert' {
        $out = Join-Path $Work 'convert'
        if (Test-Path -LiteralPath $out) { Remove-Item -LiteralPath $out -Recurse -Force }
        $code = Invoke-Tool @('convert', '--connection', $cs, '--out', $out)
        $report = Get-Content -LiteralPath (Join-Path $out 'convert-report.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $failed = @($report.versions | Where-Object { $_.status -eq 'Failed' } | ForEach-Object { "$($_.methodology) $($_.version): " + (@($_.issues | Where-Object { $_.severity -eq 'Error' } | ForEach-Object { $_.code }) -join ',') })
        Write-Phase "convert exit $code; converted $($report.converted), failed $($report.failed), skipped $($report.skipped)" @{ failed = $failed }
        # ✎ P-1123-4: одиниці C05_05/C11_07 і міст T9_07 переходять у шаблон Land 1.0.20.0 — накладка застосовується лише до того,
        # чого шаблон / константа чинної версії ще не мають (факти — з бази, рішення — Get-MsUnitsStillNeeded).
        $unitsCfg = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\..\land\land-script-units.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $facts = @{ ColumnUnits = @{}; ArgumentColumns = @{}; ConstantUnits = @{} }
        foreach ($m in $unitsCfg.methodologies.PSObject.Properties) {
            $mc = $m.Name.Replace("'", "''")
            $cur = @(Invoke-Sql "SELECT TOP 1 v.Id FROM calc.MethodologyVersion v JOIN calc.Methodology m ON m.Id = v.MethodologyId WHERE m.Code = '$mc' AND v.Status = 1 ORDER BY v.EffectiveFrom DESC, v.Id DESC")
            $sec = $m.Value.PSObject.Properties['arguments']
            if ($sec) { foreach ($a in $sec.Value.PSObject.Properties) {
                # Колонка з кодом аргументу АБО місячні колонки його псевдоніма (річний шаблон, cfg.MonthInputAlias).
                $facts.ColumnUnits["$($m.Name)|$($a.Name)"] = @(Invoke-Sql "SELECT ISNULL(u.Code, '-') FROM cfg.CalculationBinding b JOIN calc.Methodology m ON m.Id = b.MethodologyId JOIN cfg.ColumnDef c ON c.TableDefId = b.TableDefId AND c.IsDeleted = 0 AND (c.Code = '$($a.Name)' OR c.Id IN (SELECT ma.ColumnDefId FROM cfg.MonthInputAlias ma WHERE ma.TableDefId = b.TableDefId AND ma.Alias = '$($a.Name)')) LEFT JOIN uom.Unit u ON u.Id = c.UnitId WHERE m.Code = '$mc' AND b.IsActive = 1")
            } }
            $sec = $m.Value.PSObject.Properties['constants']
            if ($sec -and $cur.Count -gt 0) { foreach ($k in $sec.Value.PSObject.Properties) {
                $facts.ConstantUnits["$($m.Name)|$($k.Name)"] = [string](@(Invoke-Sql "SELECT ISNULL(u.Code, '') FROM calc.MethodologyConstant k LEFT JOIN uom.Unit u ON u.Id = k.UnitId WHERE k.MethodologyVersionId = $($cur[0]) AND k.Code = '$($k.Name)'") + @(''))[0]
            } }
            $sec = $m.Value.PSObject.Properties['argumentSources']
            if ($sec) { foreach ($s in $sec.Value.PSObject.Properties) {
                $alt = [string]$s.Value.alternative
                $gap = @(Invoke-Sql "SELECT COUNT(DISTINCT b.TableDefId), COUNT(DISTINCT CASE WHEN a.Id IS NULL AND NOT EXISTS (SELECT 1 FROM cfg.MonthInputAlias ma WHERE ma.TableDefId = b.TableDefId AND ma.Alias = '$($s.Name)') THEN b.TableDefId END) FROM cfg.CalculationBinding b JOIN calc.Methodology m ON m.Id = b.MethodologyId JOIN cfg.ColumnDef x ON x.TableDefId = b.TableDefId AND (x.Code = '$alt' OR x.Id IN (SELECT xa.ColumnDefId FROM cfg.MonthInputAlias xa WHERE xa.TableDefId = b.TableDefId AND xa.Alias = '$alt')) AND x.IsDeleted = 0 LEFT JOIN cfg.ColumnDef a ON a.TableDefId = b.TableDefId AND a.Code = '$($s.Name)' AND a.IsDeleted = 0 WHERE m.Code = '$mc' AND b.IsActive = 1")[0] -split '\|'
                $facts.ArgumentColumns["$($m.Name)|$($s.Name)"] = ([int]$gap[0] -gt 0 -and [int]$gap[1] -eq 0)
            } }
        }
        $need = Get-MsUnitsStillNeeded -Units $unitsCfg -Facts $facts
        $effective = Join-Path $Work 'land-script-units.effective.json'
        [IO.File]::WriteAllText($effective, ($need.Units | ConvertTo-Json -Depth 8), (New-Object Text.UTF8Encoding($false)))
        Write-Phase "units overlay: skipped (already in template/constants) $($need.Skipped.Count)" @{ skipped = $need.Skipped }
        if (@($need.Units.methodologies.PSObject.Properties).Count -gt 0) {
            & (Join-Path $PSScriptRoot '..\..\land\Apply-LandScriptUnits.ps1') -Dir $out -Table $effective
        } else {
            Write-Phase 'units overlay: nothing left to apply (Land template carries units and the T9_07 bridge)' $null
        }
        $pkg = Join-Path $Work 'package.json'
        if (Test-Path -LiteralPath $pkg) { Remove-Item -LiteralPath $pkg -Force }
        $code = Invoke-Tool @('pack', '--connection', $cs, '--in', $out, '--out', $pkg)
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $pkg)) { Stop-Phase "pack failed (exit $code)" $null }
        $entries = @((Get-Content -LiteralPath $pkg -Raw -Encoding UTF8 | ConvertFrom-Json).entries)
        Write-Phase "package: $($entries.Count) versions" @{ versions = @($entries | ForEach-Object { "$($_.methodologyCode) $($_.version)" }) }
    }
    'Apply' {
        $full = Read-Json 'package.json'
        # Вода та інші постійні винятки ($MsPermanentExclusions, P-1123-4) — не йдуть у пакет ніколи, кожна їхня версія.
        $permanent = @($full.entries | Where-Object { $MsPermanentExclusions -contains [string]$_.methodologyCode } | ForEach-Object { $_.methodologyCode + ' ' + $_.version })
        $kept = @($full.entries | Where-Object { $Exclude -notcontains ($_.methodologyCode + ' ' + $_.version) -and $MsPermanentExclusions -notcontains [string]$_.methodologyCode })
        $excluded = @($full.entries | Where-Object { $Exclude -contains ($_.methodologyCode + ' ' + $_.version) } | ForEach-Object { $_.methodologyCode + ' ' + $_.version })
        if ($permanent.Count -gt 0) { Write-Phase "permanently excluded (water: table formulas, P-1123-4): $($permanent -join ', ')" $null }
        $unknown = @($Exclude | Where-Object { $excluded -notcontains $_ -and $permanent -notcontains $_ })
        if ($unknown.Count -gt 0) { Stop-Phase "-Exclude names versions that are not in the package: $($unknown -join ', ')" $null }
        $body = [ordered]@{ format = $full.format; formatVersion = $full.formatVersion; entries = $kept } | ConvertTo-Json -Depth 6
        [IO.File]::WriteAllText((Join-Path $Work 'apply-package.json'), $body, (New-Object Text.UTF8Encoding($false)))
        Write-Phase "apply package: $($kept.Count) versions, excluded $($excluded.Count)" @{ excluded = $excluded }
        if ($CopyAsUser -gt 0) {
            $code = Invoke-Tool @('apply', '--connection', $cs, '--in', (Join-Path $Work 'apply-package.json'), '--as-user', $CopyAsUser, '--dry-run', '--out', (Join-Path $Work 'apply-dry.json'))
            if ($code -ne 0) { Stop-Phase "apply dry-run refused (exit $code); nothing written" $null }
            Write-Phase 'apply dry-run: all wouldApply' $null
            $code = Invoke-Tool @('apply', '--connection', $cs, '--in', (Join-Path $Work 'apply-package.json'), '--as-user', $CopyAsUser, '--out', (Join-Path $Work 'apply.json'))
            if ($code -ne 0) { Stop-Phase "apply refused (exit $code)" $null }
            Write-Phase "apply: $($kept.Count) shadow texts written (engine unchanged)" $null
            break
        }
        Connect-Api
        $dry = Invoke-Api POST '/api/v1/methodologies/script-migration/apply?dryRun=true' $body
        [IO.File]::WriteAllText((Join-Path $Work 'apply-dry.json'), $dry.Content, (New-Object Text.UTF8Encoding($false)))
        if ($dry.StatusCode -ne 200) { Stop-Phase "apply dry-run HTTP $($dry.StatusCode)" $dry.Content }
        $d = $dry.Content | ConvertFrom-Json
        $refused = @($d.entries | Where-Object { $_.status -eq 'refused' } | ForEach-Object { "$($_.methodologyCode) $($_.version): $($_.reason)" })
        if ($refused.Count -gt 0) { Stop-Phase "apply dry-run refused $($refused.Count) version(s); nothing written" @{ refused = $refused } }
        Write-Phase "apply dry-run: all $($d.applied) wouldApply" $null
        $real = Invoke-Api POST '/api/v1/methodologies/script-migration/apply?dryRun=false' $body
        [IO.File]::WriteAllText((Join-Path $Work 'apply.json'), $real.Content, (New-Object Text.UTF8Encoding($false)))
        if ($real.StatusCode -ne 200) { Stop-Phase "apply HTTP $($real.StatusCode)" $real.Content }
        $a = $real.Content | ConvertFrom-Json
        if ($a.refused -ne 0) { Stop-Phase "apply refused $($a.refused) (shadow texts of the others are written; engine unchanged)" $null }
        Write-Phase "apply: $($a.applied) shadow texts written (engine unchanged)" $null
    }
    'Parity' {
        $eligible = Join-Path $Work 'eligible.json'
        $code = Invoke-Tool @('parity', '--connection', $cs, '--out', (Join-Path $Work 'parity.json'), '--allow-unproven', '--eligible-out', $eligible)
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $eligible)) { Stop-Phase "parity is not green (exit $code): nothing will be switched" $null }
        $e = Get-Content -LiteralPath $eligible -Raw -Encoding UTF8 | ConvertFrom-Json
        Write-Phase "parity green: proven $(@($e.versionIds).Count), not proven $(@($e.notProven).Count)" @{
            proven = @($e.proven | ForEach-Object { "$($_.methodologyCode) $($_.version) values=$($_.valuesCompared)" })
            notProven = @($e.notProven | ForEach-Object { "$($_.methodologyCode) $($_.version) targets=$($_.targets) values=$($_.valuesCompared)" })
        }
    }
    'Reference' {
        # ✎ P-1123-4: паритет на ЕТАЛОННИХ даних старої системи (ECR, лише читання) для версій, яких дані live не доводять.
        $cases = Join-Path $Work 'ref-cases.json'
        & (Join-Path $PSScriptRoot 'Build-MsReferenceCases.ps1') -Server $Server -ReferenceDatabase $ReferenceDatabase -Database $Database -Out $cases
        $code = Invoke-Tool @('parity-cases', '--connection', $cs, '--in', $cases, '--out', (Join-Path $Work 'ref-parity.json'),
            '--eligible-out', (Join-Path $Work 'ref-eligible.json'), '--golden-out', (Join-Path $Work 'golden-proposals.json'))
        & (Join-Path $PSScriptRoot 'Build-MsBorrowedRules.ps1') -Server $Server -ReferenceDatabase $ReferenceDatabase -Database $Database -Out (Join-Path $Work 'borrowed-rules.json')
        $rp = Read-Json 'ref-parity.json'
        if (@($rp.mismatches).Count -gt 0) { Stop-Phase "reference parity has $(@($rp.mismatches).Count) mismatch(es): nothing will be switched" $null }
        $proven = @($rp.versions | Where-Object { $null -eq $_.reason })
        Write-Phase "reference parity ($ReferenceDatabase): proven $($proven.Count) of $(@($rp.versions).Count), rows $((@($rp.versions) | Measure-Object rows -Sum).Sum), values $((@($rp.versions) | Measure-Object valuesCompared -Sum).Sum)" @{
            proven = @($proven | ForEach-Object { "$($_.methodologyCode) $($_.version) values=$($_.valuesCompared)" })
            notProven = @($rp.versions | Where-Object { $null -ne $_.reason } | ForEach-Object { "$($_.methodologyCode) $($_.version): $($_.reason)" })
        }
    }
    'GoldenVersions' {
        # ✎ P-1123-4: нові версії з очікуваннями золотого набору з рядка-еталона ECR (PSS-23 вимагає очікувань для перемикання).
        $proposals = Join-Path $Work 'golden-proposals.json'
        if (-not (Test-Path -LiteralPath $proposals)) { Stop-Phase 'missing golden-proposals.json (run Reference first)' $null }
        if ($CopyAsUser -gt 0) {
            $code = Invoke-Tool @('golden-versions', '--connection', $cs, '--in', $proposals, '--borrowed', (Join-Path $Work 'borrowed-rules.json'), '--as-user', $CopyAsUser, '--out', (Join-Path $Work 'golden-versions.json'))
            # Невдалі (BLOCKED: публікація відмовила, чернетку видалено) лишаються на Formulas; решта версій уже опублікована.
            if ($code -eq 2) { Stop-Phase "golden-versions environment error (exit $code)" $null }
            $gv = Read-Json 'golden-versions.json'
            Write-Phase "golden versions created and published (copy): $(@($gv.created).Count)$(if ($code -ne 0) { '; some are BLOCKED (see tool output) and stay on Formulas' } else { '' })" $null
            break
        }
        $gp = Get-Content -LiteralPath $proposals -Raw -Encoding UTF8 | ConvertFrom-Json
        $created = New-Object System.Collections.Generic.List[object]
        $byVid = [ordered]@{}
        foreach ($x in $gp.versions) { $byVid[[int]$x.versionId] = @{ P = $x; R = @() } }
        $bp = Join-Path $Work 'borrowed-rules.json'
        if (Test-Path -LiteralPath $bp) { foreach ($x in (Read-Json 'borrowed-rules.json').versions) { if (@($x.rules).Count -gt 0) { if (-not $byVid.Contains([int]$x.versionId)) { $byVid[[int]$x.versionId] = @{ P = $null; R = @() } }; $byVid[[int]$x.versionId].R = @($x.rules) } } }
        foreach ($vid in @($byVid.Keys)) {
            $pr = $byVid[$vid].P; $extraRules = @($byVid[$vid].R)
            # Випадок без очікувань АБО з документом, якого в базі немає (ворота: «тест не має періоду») — замінюється рядком ECR.
            $empty = @(Invoke-Sql "SELECT t.Code FROM calc.TestCase t WHERE t.MethodologyVersionId = $vid AND (t.ExpectedJson IS NULL OR REPLACE(REPLACE(REPLACE(t.ExpectedJson, ' ', ''), CHAR(10), ''), CHAR(13), '') = '{}' OR NOT EXISTS (SELECT 1 FROM doc.Document d WHERE d.Id = TRY_CAST(JSON_VALUE(t.InputJson, '$.documentId') AS bigint)))")
            if ($empty.Count -eq 0 -and $extraRules.Count -eq 0) { continue }
            $mcode = [string](@(Invoke-Sql "SELECT m.Code FROM calc.MethodologyVersion v JOIN calc.Methodology m ON m.Id = v.MethodologyId WHERE v.Id = $vid")[0])
            if ($empty.Count -gt 0 -and $null -eq $pr) { Write-Phase "BLOCKED ${mcode}: golden set without expectations and no ECR reference row" $null; continue }
            $info = (@(Invoke-Sql "SELECT v.MethodologyId, CONVERT(varchar(10), DATEADD(day, 1, v.EffectiveFrom), 23), v.Level FROM calc.MethodologyVersion v WHERE v.Id = $vid")[0]) -split '\|'
            $numbers = @(Invoke-Sql "SELECT Version FROM calc.MethodologyVersion WHERE MethodologyId = $($info[0])")
            $max = (@($numbers | Where-Object { $_ -match '^V(\d+)$' } | ForEach-Object { [int]($_.Substring(1)) }) + @(0) | Measure-Object -Maximum).Maximum
            $number = 'V' + ($max + 1)
            Connect-Api
            $level = if ([int]$info[2] -eq 3) { 'Module' } else { 'Configuration' }
            $r = Invoke-Api POST "/api/v1/methodologies/$($info[0])/versions" (@{ versionNumber = $number; copyFromVersionId = $vid; level = $level; engine = 'Formulas' } | ConvertTo-Json)
            if ($r.StatusCode -ne 200) { Stop-Phase "create version $mcode $number HTTP $($r.StatusCode)" $r.Content }
            $newId = [int]($r.Content | ConvertFrom-Json).id
            $inputJson = if ($null -eq $pr) { $null } else { @{ documentId = [long]$pr.documentId; periodKey = @{ value = [int]$pr.periodKey }; arguments = @($pr.arguments | ForEach-Object { @{ argumentCode = $_.argumentCode; value = $_.value; valueString = $_.valueString; unitId = $null; entryId = $_.entryId } }) } | ConvertTo-Json -Depth 8 -Compress }
            $expectedJson = if ($null -eq $pr) { $null } else { $pr.expected | ConvertTo-Json -Compress }
            foreach ($tc in $empty) {
                $put = Invoke-Api PUT "/api/v1/methodologies/$($info[0])/versions/$newId/tests/$tc" (@{ inputJson = $inputJson; expectedJson = $expectedJson; tolerance = 0.000000001 } | ConvertTo-Json -Depth 4)
                if ($put.StatusCode -ne 200) { Stop-Phase "test $tc of $mcode $number HTTP $($put.StatusCode)" $put.Content }
            }
            foreach ($rule in $extraRules) {
                $put = Invoke-Api PUT "/api/v1/methodologies/$($info[0])/versions/$newId/rules/$($rule.code)" (@{ matchJson = [string]$rule.matchJson; priority = [int]$rule.priority; isActive = $true } | ConvertTo-Json)
                if ($put.StatusCode -ne 200) { Stop-Phase "rule $($rule.code) of $mcode $number HTTP $($put.StatusCode)" $put.Content }
            }
            if ($ReviewerCredentialFile) { Connect-Api $ReviewerCredentialFile }
            $pub = Invoke-Api POST "/api/v1/methodologies/$($info[0])/versions/$newId/publish" (@{ changeReason = ("W15z D-571 (P-1123-4): " + $(if ($empty.Count -gt 0) { "golden expectations from ECR reference row $($pr.caseKey)" } else { "golden set unchanged" }) + $(if ($extraRules.Count -gt 0) { "; $($extraRules.Count) rule(s) the old system applied (ECR CalcResult)" } else { "" })); effectiveFrom = $info[1] } | ConvertTo-Json)
            if ($pub.StatusCode -ne 200) {
                # Чернетку, яку не вдалося опублікувати, прибираємо (повтор фази не множить версії-сироти); решта версій іде далі.
                if ($ReviewerCredentialFile) { Connect-Api }
                $null = Invoke-Api DELETE "/api/v1/methodologies/$($info[0])/versions/$newId" $null
                Write-Phase "BLOCKED $mcode ${number}: publish HTTP $($pub.StatusCode) — draft deleted, version stays on Formulas" $pub.Content
                continue
            }
            $created.Add([ordered]@{ methodologyCode = $mcode; fromVersionId = $vid; versionId = $newId; version = $number; effectiveFrom = $info[1]; tests = $empty; rules = $extraRules.Count })
            Write-Phase "golden version $mcode $number (from $($info[1])), cases with ECR reference $($empty.Count), borrowed rules $($extraRules.Count)" $null
        }
        [IO.File]::WriteAllText((Join-Path $Work 'golden-versions.json'), (@{ created = @($created.ToArray()) } | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
        Write-Phase "golden versions created and published: $($created.Count)" $null
    }
    'Switch' {
        # Кандидати — чинні опубліковані версії (Configuration і Module) з тіньовим текстом; стан золотого набору — з бази.
        $rows = @(Invoke-Sql @"
SELECT v.Id, m.Code, v.Version, v.Engine, LOWER(CONVERT(varchar(64), v.ScriptHash, 2)),
       CASE WHEN NOT EXISTS (SELECT 1 FROM calc.TestCase t WHERE t.MethodologyVersionId = v.Id) THEN 'none'
            WHEN EXISTS (SELECT 1 FROM calc.TestCase t WHERE t.MethodologyVersionId = v.Id AND (t.ExpectedJson IS NULL OR REPLACE(REPLACE(REPLACE(t.ExpectedJson, ' ', ''), CHAR(10), ''), CHAR(13), '') = '{}')) THEN 'empty'
            WHEN EXISTS (SELECT 1 FROM calc.TestCase t WHERE t.MethodologyVersionId = v.Id AND NOT EXISTS (SELECT 1 FROM doc.Document d WHERE d.Id = TRY_CAST(JSON_VALUE(t.InputJson, '$.documentId') AS bigint))) THEN 'invalid'
            ELSE 'ok' END
FROM calc.MethodologyVersion v JOIN calc.Methodology m ON m.Id = v.MethodologyId
WHERE v.Status = 1 AND m.Kind <> 2 AND v.ScriptText IS NOT NULL
  AND v.EffectiveFrom = (SELECT MAX(x.EffectiveFrom) FROM calc.MethodologyVersion x WHERE x.MethodologyId = v.MethodologyId AND x.Status = 1)
"@)
        $candidates = @($rows | ForEach-Object { $c = $_ -split '\|'; @{ VersionId = [int]$c[0]; Code = $c[1]; Version = $c[2]; Engine = [int]$c[3]; DbHash = $c[4]; GoldenExpectations = $c[5] } })
        $live = @{}; $ref = @{}; $mism = New-Object System.Collections.Generic.List[int]
        if (Test-Path -LiteralPath (Join-Path $Work 'eligible.json')) { foreach ($v in (Read-Json 'eligible.json').proven) { $live[[int]$v.versionId] = [string]$v.scriptHash } }
        if (Test-Path -LiteralPath (Join-Path $Work 'ref-eligible.json')) { foreach ($v in (Read-Json 'ref-eligible.json').proven) { $ref[[int]$v.versionId] = [string]$v.scriptHash } }
        foreach ($f in 'parity.json', 'ref-parity.json') {
            if (Test-Path -LiteralPath (Join-Path $Work $f)) { foreach ($m in @((Read-Json $f).mismatches)) { $mism.Add([int]$m.versionId) } }
        }
        $plan = Get-MsSwitchPlan -Candidates $candidates -Live $live -Reference $ref -Mismatched @($mism.ToArray())
        [IO.File]::WriteAllText((Join-Path $Work 'switch-plan.json'), ($plan | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
        Write-Phase "plan: live $($plan.Live.Count), reference $($plan.Reference.Count), golden $($plan.Golden.Count), already Script $($plan.Already.Count), stay on Formulas $($plan.Stay.Count)" @{
            stay = @($plan.Stay | ForEach-Object { "$($_.Code) $($_.Version): $($_.Reason)" })
        }
        $byId = @{}; foreach ($c in $candidates) { $byId[$c.VersionId] = $c }
        $done = New-Object System.Collections.Generic.List[object]
        $prev = Join-Path $Work 'switch-result.json'
        if (Test-Path -LiteralPath $prev) { foreach ($v in @((Read-Json 'switch-result.json').versions)) { $done.Add($v) } }
        $thisRun = New-Object System.Collections.Generic.List[int]
        # Кожна версія — окремий атомарний switch: відмова воріт однієї (золотий набір, склад, цикл) не блокує інших і нічого не
        # змінює в ній самій; причина журналу називає джерело доказу. Бібліотек з імпортерами на live немає (calc.MethodologyImport 0);
        # якщо з'являться — switch сам відмовить switchGroupIncomplete, і версія лишиться на Formulas з цією причиною.
        $blocked = New-Object System.Collections.Generic.List[string]
        foreach ($g in @(@('live', $plan.Live), @('reference', $plan.Reference), @('golden', $plan.Golden))) {
            foreach ($id in @($g[1])) {
                $why = "$Reason; proof=$($g[0])" + $(if ($g[0] -eq 'reference') { ":$ReferenceDatabase" } else { '' })
                $ok = $true; $detail = $null
                if ($CopyAsUser -gt 0) {
                    [IO.File]::WriteAllText((Join-Path $Work 'switch-one.json'), (@{ versionIds = @($id) } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
                    $code = Invoke-Tool @('switch', '--connection', $cs, '--in', (Join-Path $Work 'switch-one.json'), '--engine', 'Script', '--reason', $why, '--as-user', $CopyAsUser)
                    $ok = ($code -eq 0)
                } else {
                    if (-not $script:session) { Connect-Api }
                    $r = Invoke-Api POST '/api/v1/methodologies/script-migration/switch' (@{ versionIds = @($id); engine = 'Script'; reason = $why } | ConvertTo-Json -Depth 4)
                    $ok = ($r.StatusCode -eq 200); $detail = $r.Content
                }
                if (-not $ok) { $blocked.Add("$($byId[$id].Code) $($byId[$id].Version) ($($g[0]))"); Write-Phase "BLOCKED $($byId[$id].Code) $($byId[$id].Version): switch refused, stays on Formulas" $detail; continue }
                $thisRun.Add($id); $done.Add([ordered]@{ versionId = $id; methodologyCode = $byId[$id].Code; version = $byId[$id].Version; proof = $g[0] })
            }
            Write-Phase "switched to Script ($($g[0])): $(@($g[1] | Where-Object { $thisRun -contains $_ }).Count) of $(@($g[1]).Count)" $null
        }
        if ($blocked.Count -gt 0) { Write-Phase "switch refused (stay on Formulas): $($blocked -join ', ')" $null }
        [IO.File]::WriteAllText($prev, (@{ engine = 'Script'; versions = @($done.ToArray()) } | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))
        if ($thisRun.Count -eq 0) { Write-Phase "nothing to switch (already Script: $($plan.Already.Count))" $null }
    }    'Verify' {
        $e = Read-Json 'eligible.json'
        $sr = Read-Json 'switch-result.json'
        $ids = @($sr.versions | ForEach-Object { [int]$_.versionId })
        $bad = @(Get-EngineState $ids | Where-Object { [int](($_ -split '\|')[1]) -ne 1 })
        if ($bad.Count -gt 0) { Stop-Phase "not on Script: $($bad -join '; ')" $null }
        $noFields = @(Invoke-Sql ("SELECT v.Id FROM calc.MethodologyVersion v WHERE v.Id IN (" + ($ids -join ',') + ") AND NOT EXISTS (SELECT 1 FROM calc.MethodologyReadField f WHERE f.MethodologyVersionId = v.Id)"))
        if ($noFields.Count -gt 0) { Stop-Phase "D-568 read fields not recorded for: $($noFields -join ',')" $null }
        $others = @(Invoke-Sql ("SELECT COUNT(*) FROM calc.MethodologyVersion WHERE Engine = 1 AND Id NOT IN (" + ($ids -join ',') + ")"))
        Write-Phase "engine Script on $($ids.Count) versions, D-568 recorded on all; other Script versions: $($others[0])" $null
        if ($Sample -gt 0 -and $BaseUrl) {
            Connect-Api
            $picked = @($e.proven | Where-Object { $ids -contains [int]$_.versionId } | Sort-Object { - [int]$_.valuesCompared } | Select-Object -First $Sample)
            foreach ($v in $picked) {
                $t = @(Invoke-Sql ("SELECT TOP 1 m.Id, i.DocumentId, i.PeriodKey FROM calc.MethodologyVersion v JOIN calc.Methodology m ON m.Id = v.MethodologyId JOIN cfg.CalculationBinding b ON b.MethodologyId = m.Id AND b.IsActive = 1 JOIN doc.TableInstance i ON i.TableDefId = b.TableDefId WHERE v.Id = $($v.versionId) ORDER BY i.PeriodKey DESC"))
                if ($t.Count -eq 0) { continue }
                $c = $t[0] -split '\|'
                $r = Invoke-Api POST "/api/v1/methodologies/$($c[0])/versions/$($v.versionId)/script/parity" (@{ documentId = [long]$c[1]; periodKey = [int]$c[2] } | ConvertTo-Json)
                if ($r.StatusCode -ne 200) { Stop-Phase "sample parity HTTP $($r.StatusCode) for $($v.methodologyCode)" $r.Content }
                $p = $r.Content | ConvertFrom-Json
                if ($p.mismatchCount -ne 0) { Stop-Phase "sample parity mismatches for $($v.methodologyCode): $($p.mismatchCount) — run Rollback" $r.Content }
                Write-Phase "sample parity $($v.methodologyCode) $($v.version) doc $($c[1]) period $($c[2]): 0 mismatches" $null
            }
        }
    }
    'Rollback' {
        $p = Join-Path $Work 'switch-result.json'
        $ids = if (Test-Path -LiteralPath $p) { @((Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json).versions | ForEach-Object { [int]$_.versionId }) } else { @((Read-Json 'eligible.json').versionIds | ForEach-Object { [int]$_ }) }
        $onScript = @(Get-EngineState $ids | Where-Object { [int](($_ -split '\|')[1]) -eq 1 } | ForEach-Object { [int](($_ -split '\|')[0]) })
        if ($onScript.Count -eq 0) { Write-Phase 'nothing to roll back: none of the versions is on Script' $null; break }
        if ($CopyAsUser -gt 0) {
            [IO.File]::WriteAllText((Join-Path $Work 'rollback-ids.json'), (@{ versionIds = $onScript } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
            $code = Invoke-Tool @('switch', '--connection', $cs, '--in', (Join-Path $Work 'rollback-ids.json'), '--engine', 'Formulas', '--reason', "ROLLBACK: $Reason", '--as-user', $CopyAsUser, '--out', (Join-Path $Work 'rollback-result.json'))
            if ($code -ne 0) { Stop-Phase "rollback failed (exit $code)" $null }
        } else {
        Connect-Api
        $r = Invoke-Api POST '/api/v1/methodologies/script-migration/switch' (@{ versionIds = $onScript; engine = 'Formulas'; reason = "ROLLBACK: $Reason" } | ConvertTo-Json -Depth 4)
        [IO.File]::WriteAllText((Join-Path $Work 'rollback-result.json'), $r.Content, (New-Object Text.UTF8Encoding($false)))
        if ($r.StatusCode -ne 200) { Stop-Phase "rollback HTTP $($r.StatusCode)" $r.Content }
        }
        $still = @(Get-EngineState $onScript | Where-Object { [int](($_ -split '\|')[1]) -ne 0 })
        if ($still.Count -gt 0) { Stop-Phase "still not on Formulas: $($still -join '; ')" $null }
        Write-Phase "rolled back to Formulas: $($onScript.Count)" @{ versionIds = $onScript }
    }
}
Write-Phase ("done in {0:n1} s" -f $sw.Elapsed.TotalSeconds) $null
