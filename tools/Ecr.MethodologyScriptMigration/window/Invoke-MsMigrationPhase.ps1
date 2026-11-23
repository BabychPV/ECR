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
      Rollback  — POST switch Formulas на версії з switch-result.json; ворота: рушій Formulas у кожної, таблиці читання зняті.
    Усе, що зроблено, — у теці -Work (phase-log.ndjson, *.json відповідей) і в журналі застосунку (aud.PublicationEvent).
.PARAMETER Stage           Convert | Apply | Parity | Switch | Verify | Rollback.
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
    [Parameter(Mandatory)][ValidateSet('Convert', 'Apply', 'Parity', 'Switch', 'Verify', 'Rollback')][string]$Stage,
    [Parameter(Mandatory)][string]$Work,
    [string]$Server = 'localhost',
    [Parameter(Mandatory)][string]$Database,
    [string]$BaseUrl,
    [string]$CredentialFile,
    [string]$ToolDll,
    [string]$Reason = 'W15z: methodologies moved to ECR Script (D-552, D-571); parity proven on live data',
    [int]$Sample = 5,
    [string[]]$Exclude = @(),
    [int]$CopyAsUser = 0
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
# powershell -File передає «a,b» одним рядком: розбити за комами.
$Exclude = @($Exclude | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
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
    $rows = & sqlcmd -S $Server -E -d $Database -b -h -1 -W -s '|' -Q ("SET NOCOUNT ON; " + $query)
    if ($LASTEXITCODE -ne 0) { Stop-Phase "sqlcmd failed: $query" $rows }
    return @($rows | Where-Object { $_ -and $_.Trim() })
}
function Read-Json([string]$name) {
    $p = Join-Path $Work $name
    if (-not (Test-Path -LiteralPath $p)) { Stop-Phase "missing $name (run the previous stage)" $null }
    return Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json
}
$script:session = $null
function Connect-Api {
    if (-not $BaseUrl -or -not $CredentialFile) { Stop-Phase 'BaseUrl and CredentialFile are required for this stage' $null }
    $pair = (Get-Content -LiteralPath $CredentialFile -Raw -Encoding UTF8).Trim() -split "`t", 2
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
        & (Join-Path $PSScriptRoot '..\..\land\Apply-LandScriptUnits.ps1') -Dir $out
        $pkg = Join-Path $Work 'package.json'
        if (Test-Path -LiteralPath $pkg) { Remove-Item -LiteralPath $pkg -Force }
        $code = Invoke-Tool @('pack', '--connection', $cs, '--in', $out, '--out', $pkg)
        if ($code -ne 0 -or -not (Test-Path -LiteralPath $pkg)) { Stop-Phase "pack failed (exit $code)" $null }
        $entries = @((Get-Content -LiteralPath $pkg -Raw -Encoding UTF8 | ConvertFrom-Json).entries)
        Write-Phase "package: $($entries.Count) versions" @{ versions = @($entries | ForEach-Object { "$($_.methodologyCode) $($_.version)" }) }
    }
    'Apply' {
        $full = Read-Json 'package.json'
        $kept = @($full.entries | Where-Object { $Exclude -notcontains ($_.methodologyCode + ' ' + $_.version) })
        $excluded = @($full.entries | Where-Object { $Exclude -contains ($_.methodologyCode + ' ' + $_.version) } | ForEach-Object { $_.methodologyCode + ' ' + $_.version })
        $unknown = @($Exclude | Where-Object { $excluded -notcontains $_ })
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
    'Switch' {
        $e = Read-Json 'eligible.json'
        $want = @{}; foreach ($v in $e.proven) { $want[[int]$v.versionId] = [string]$v.scriptHash }
        $ids = @($want.Keys | Sort-Object)
        $state = Get-EngineState $ids
        $already = @(); $pending = @()
        foreach ($row in $state) {
            $c = $row -split '\|'; $id = [int]$c[0]
            if ($c[2] -ne $want[$id]) { Stop-Phase "version $id script changed after parity (db $($c[2]) vs proven $($want[$id])): rerun Parity" $null }
            if ([int]$c[1] -eq 1) { $already += $id } else { $pending += $id }
        }
        if ($state.Count -ne $ids.Count) { Stop-Phase "some versions of eligible.json are missing in the database" $null }
        # PSS-23: перемикання на Script вимагає зеленого золотого набору з очікуваннями. Версію без жодного випадку чи з випадком
        # без очікувань (ExpectedJson = {}) ворота однаково відхилять і відкотять УСЮ групу — тож вона лишається на Formulas і
        # називається у звіті (рішення людини: нові версії з еталонами, D-40), а решта перемикається.
        $noGolden = @(Invoke-Sql ("SELECT v.Id FROM calc.MethodologyVersion v WHERE v.Id IN (" + ($ids -join ',') + ") AND (NOT EXISTS (SELECT 1 FROM calc.TestCase t WHERE t.MethodologyVersionId = v.Id) OR EXISTS (SELECT 1 FROM calc.TestCase t WHERE t.MethodologyVersionId = v.Id AND (t.ExpectedJson IS NULL OR REPLACE(REPLACE(REPLACE(t.ExpectedJson, ' ', ''), CHAR(10), ''), CHAR(13), '') = '{}')))") | ForEach-Object { [int]$_ })
        if ($noGolden.Count -gt 0) {
            $names = @($e.proven | Where-Object { $noGolden -contains [int]$_.versionId } | ForEach-Object { "$($_.methodologyCode) $($_.version)" })
            Write-Phase "golden set without expectations (PSS-23), stays on Formulas: $($names -join ', ')" @{ versionIds = $noGolden }
            $pending = @($pending | Where-Object { $noGolden -notcontains $_ })
        }
        if ($pending.Count -eq 0) { Write-Phase "nothing to do: all $($already.Count) already on Script" $null; break }
        if ($already.Count -gt 0) { Write-Phase "already on Script (skipped): $($already -join ',')" $null }
        if ($CopyAsUser -gt 0) {
            [IO.File]::WriteAllText((Join-Path $Work 'switch-ids.json'), (@{ versionIds = $pending } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
            $code = Invoke-Tool @('switch', '--connection', $cs, '--in', (Join-Path $Work 'switch-ids.json'), '--engine', 'Script', '--reason', $Reason, '--as-user', $CopyAsUser, '--out', (Join-Path $Work 'switch-result.json'))
            if ($code -ne 0) { Stop-Phase "switch refused (exit $code); the transaction rolled back, nothing switched" $null }
            Write-Phase "switched to Script: $($pending.Count)" @{ versionIds = $pending }
            break
        }
        Connect-Api
        $r = Invoke-Api POST '/api/v1/methodologies/script-migration/switch' (@{ versionIds = $pending; engine = 'Script'; reason = $Reason } | ConvertTo-Json -Depth 4)
        [IO.File]::WriteAllText((Join-Path $Work 'switch-result.json'), $r.Content, (New-Object Text.UTF8Encoding($false)))
        if ($r.StatusCode -ne 200) { Stop-Phase "switch refused HTTP $($r.StatusCode); the transaction rolled back, nothing switched" $r.Content }
        Write-Phase "switched to Script: $($pending.Count)" @{ versionIds = $pending }
    }
    'Verify' {
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
