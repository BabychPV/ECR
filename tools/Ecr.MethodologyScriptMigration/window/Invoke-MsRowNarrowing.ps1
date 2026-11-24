<#
.SYNOPSIS
    c11-10 (P-1124-2, ф.2): нова Script-версія методології без умов відбору рядків, які її ж правило категорії завжди відкидає.
.DESCRIPTION
    Причина (ECW_C11_10 V4, W15z D-571 г): 28 «позичених» правил C11_01/02/04/05 відбирають рядки 9_04/9_07 з NAE поза групами
    C11_10 -> на кожному такому рядку лише CategoryRuleRejected («категорію не визначено»), жодного значення. Логіка відбору умов —
    MsRowNarrowing.Lib.ps1 (тест MsRowNarrowingLibTests).

    Правила-рядки клону (calc.MethodologyRule) з тими самими значеннями, що й зняті умови, вимикаються в чернетці (PUT rule
    isActive=false — єдина дозволена правка правила версії-скрипта), рівно -ExpectRemoved; причина публікації це називає.
    Шлях — звичайний продуктовий API (як стадія GoldenVersions): клон чинної версії -> текст скрипта -> публікація з датою чинності
    «чинна + 1 день». Значення не змінюються за побудовою: прибрані рядки не давали жодного результату. Перерахунок відкритих періодів —
    окремий крок вікна (нічний або recalc), не тут.

    Ворота (будь-яке не так -> СТОП, нічого не записано): чинна версія — Script і має номер -ExpectFromVersion; хеш тексту з API = хеш
    у БД; прибирається рівно -ExpectRemoved умов; збережений текст чернетки без діагностик і з тим самим хешем. -DryRun — лише файли
    в -Work (до/після/перелік). БД читається лише SELECT (ідентифікатори й стан); усі записи — через API.
.PARAMETER Methodology       Код методології (за замовчуванням ECW_C11_10).
.PARAMETER ExpectFromVersion Номер чинної версії, з якої клонуємо (V4 після W15z).
.PARAMETER ExpectRemoved     Скільки умов має прибратися (28 для C11_10 V4).
#>
param(
    [string]$Server = 'localhost',
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][string]$Work,
    [string]$BaseUrl,
    [string]$CredentialFile,
    [string]$ReviewerCredentialFile,
    [string]$Methodology = 'ECW_C11_10',
    [string]$ExpectFromVersion = 'V4',
    [int]$ExpectRemoved = 28,
    [switch]$DryRun
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'MsRowNarrowing.Lib.ps1')
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$log = Join-Path $Work 'row-narrowing-log.ndjson'
$utf8 = New-Object Text.UTF8Encoding($false)

function Write-Step([string]$what, $data) {
    $line = [ordered]@{ at = (Get-Date).ToUniversalTime().ToString('o'); database = $Database; methodology = $Methodology; what = $what; data = $data } | ConvertTo-Json -Compress -Depth 6
    [IO.File]::AppendAllText($log, $line + "`n", $utf8)
    Write-Host "[RowNarrowing] $what"
}
function Stop-Step([string]$why, $data) { Write-Step "STOP: $why" $data; throw "RowNarrowing stopped: $why" }
function Invoke-Sql([string]$query) {
    $rows = & sqlcmd -S $Server -E -d $Database -b -h -1 -W -s '|' -Q ("SET NOCOUNT ON; " + ($query -replace '\s+', ' '))
    if ($LASTEXITCODE -ne 0) { Stop-Step "sqlcmd failed: $query" $rows }
    return @($rows | Where-Object { $_ -and $_.Trim() })
}
$script:session = $null
function Invoke-Api([string]$method, [string]$path, [string]$body) {
    $a = @{ Uri = "$BaseUrl$path"; Method = $method; WebSession = $script:session; UseBasicParsing = $true; ContentType = 'application/json; charset=utf-8'; TimeoutSec = 1800 }
    if ($body) { $a.Body = [Text.Encoding]::UTF8.GetBytes($body) }
    try { $r = Invoke-WebRequest @a; return [pscustomobject]@{ StatusCode = [int]$r.StatusCode; Content = $r.Content } }
    catch {
        $resp = $_.Exception.Response
        if ($resp) { $sr = New-Object IO.StreamReader($resp.GetResponseStream()); return [pscustomobject]@{ StatusCode = [int]$resp.StatusCode; Content = $sr.ReadToEnd() } }
        throw
    }
}
function Connect-Api([string]$file) {
    if (-not $BaseUrl -or -not $file) { Stop-Step 'BaseUrl and CredentialFile are required' $null }
    $pair = (Get-Content -LiteralPath $file -Raw -Encoding UTF8).Trim() -split "`t", 2
    if ($pair.Count -ne 2) { Stop-Step 'credential file must be "user<TAB>password"' $null }
    $script:session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $r = Invoke-Api POST '/api/v1/login/local' (@{ userName = $pair[0]; password = $pair[1] } | ConvertTo-Json)
    if ($r.StatusCode -ne 200) { Stop-Step "login failed ($($r.StatusCode))" $null }
    Write-Step "login ok as $($pair[0])" $null
}

$mc = $Methodology.Replace("'", "''")
$cur = @(Invoke-Sql "SELECT TOP 1 v.MethodologyId, v.Id, v.Version, v.Engine, CONVERT(varchar(10), v.EffectiveFrom, 23), CONVERT(varchar(10), DATEADD(day, 1, v.EffectiveFrom), 23), LOWER(CONVERT(varchar(64), v.ScriptHash, 2)) FROM calc.MethodologyVersion v JOIN calc.Methodology m ON m.Id = v.MethodologyId WHERE m.Code = '$mc' AND v.Status = 1 ORDER BY v.EffectiveFrom DESC, v.Id DESC")
if ($cur.Count -eq 0) { Stop-Step 'no published version' $null }
$f = $cur[0] -split '\|'
$mid = [int]$f[0]; $vid = [int]$f[1]; $ver = $f[2]; $engine = [int]$f[3]; $from = $f[5]; $dbHash = $f[6]
if ($ver -ne $ExpectFromVersion) { Stop-Step "current version is $ver, expected $ExpectFromVersion (already narrowed, or another version was published)" @{ versionId = $vid } }
if ($engine -ne 1) { Stop-Step "current version $ver is not Script (engine $engine)" $null }
$drafts = @(Invoke-Sql "SELECT v.Version FROM calc.MethodologyVersion v WHERE v.MethodologyId = $mid AND v.Status = 0")
if ($drafts.Count -gt 0) { Stop-Step "methodology has draft version(s) $($drafts -join ', '): resolve them first" $null }
$numbers = @(Invoke-Sql "SELECT Version FROM calc.MethodologyVersion WHERE MethodologyId = $mid")
$next = 'V' + ((@($numbers | Where-Object { $_ -match '^V(\d+)$' } | ForEach-Object { [int]($_.Substring(1)) }) + @(0) | Measure-Object -Maximum).Maximum + 1)

Connect-Api $CredentialFile
$g = Invoke-Api GET "/api/v1/methodologies/$mid/versions/$vid/script" $null
if ($g.StatusCode -ne 200) { Stop-Step "GET script HTTP $($g.StatusCode)" $g.Content }
$src = $g.Content | ConvertFrom-Json
if ([string]$src.hash -ne $dbHash) { Stop-Step "script hash from API $($src.hash) != DB $dbHash" $null }
$plan = Remove-MsUnreachableRowClauses -ScriptText ([string]$src.text) -NewVersion $next -EffectiveFrom $from `
    -Note "c11-10: removed $ExpectRemoved row clause(s) whose NAE the category rule always rejects (W15z borrowed rules; no value came from them)"
[IO.File]::WriteAllText((Join-Path $Work "$Methodology-$ver.ecrs"), [string]$src.text, $utf8)
[IO.File]::WriteAllText((Join-Path $Work "$Methodology-$next.ecrs"), $plan.Text, $utf8)
[IO.File]::WriteAllText((Join-Path $Work 'row-narrowing-removed.json'), (@{ methodology = $Methodology; fromVersionId = $vid; fromVersion = $ver; newVersion = $next; removed = $plan.Removed } | ConvertTo-Json -Depth 5), $utf8)
$byTable = @($plan.Removed | Group-Object Table | ForEach-Object { "$($_.Name) $($_.Count)" })
if ($plan.Removed.Count -ne $ExpectRemoved) { Stop-Step "would remove $($plan.Removed.Count) clause(s), expected $ExpectRemoved" @{ byTable = $byTable } }
Write-Step "plan: $Methodology $ver (id $vid) -> $next from $from, remove $($plan.Removed.Count) clause(s)" @{ byTable = $byTable }
if ($DryRun) { Write-Step 'dry run: nothing written' $null; return }

$level = [string](@(Invoke-Sql "SELECT CASE v.Level WHEN 3 THEN 'Module' ELSE 'Configuration' END FROM calc.MethodologyVersion v WHERE v.Id = $vid")[0])
$r = Invoke-Api POST "/api/v1/methodologies/$mid/versions" (@{ versionNumber = $next; copyFromVersionId = $vid; level = $level } | ConvertTo-Json)
if ($r.StatusCode -ne 200) { Stop-Step "create version $next HTTP $($r.StatusCode)" $r.Content }
$newId = [int]($r.Content | ConvertFrom-Json).id
$clone = (Invoke-Api GET "/api/v1/methodologies/$mid/versions/$newId/script" $null).Content | ConvertFrom-Json
$put = Invoke-Api PUT "/api/v1/methodologies/$mid/versions/$newId/script" (@{ text = $plan.Text; expectedHash = [string]$clone.hash } | ConvertTo-Json)
if ($put.StatusCode -ne 200) { Stop-Step "save script of $next (draft $newId) HTTP $($put.StatusCode); draft left for review" $put.Content }
$saved = $put.Content | ConvertFrom-Json
if (@($saved.diagnostics).Count -gt 0) { Stop-Step "script of $next (draft $newId) has $(@($saved.diagnostics).Count) diagnostic(s); draft left for review" @($saved.diagnostics | ForEach-Object { "$($_.code) L$($_.line): $($_.message)" }) }
if ([string]$saved.text -ne $plan.Text) { Stop-Step "saved text of draft $newId differs from the plan" $null }
# Правила (calc.MethodologyRule) клону з тими самими значеннями, що й зняті умови, — вимкнути: рядки відбирає текст, а ці правила
# інакше лишались би джерелом підказок, покриття й вимог обов'язкових входів для рядків, яких нова версія не рахує.
$rr = Invoke-Api GET "/api/v1/methodologies/$mid/versions/$newId/rules" $null
if ($rr.StatusCode -ne 200) { Stop-Step "GET rules of draft $newId HTTP $($rr.StatusCode); draft left for review" $rr.Content }
# PowerShell 5.1: ConvertFrom-Json віддає JSON-масив ОДНИМ об'єктом — розгорнути явно.
$rules = @(($rr.Content | ConvertFrom-Json) | ForEach-Object { $_ })
$retire = @(Get-MsRulesOfRemovedClauses -Rules $rules -Removed $plan.Removed)
if ($retire.Count -ne $ExpectRemoved) { Stop-Step "draft $newId has $($retire.Count) rule row(s) matching the removed clauses, expected $ExpectRemoved; draft left for review" @{ codes = $retire } }
foreach ($code in $retire) {
    $rule = $rules | Where-Object { $_.code -eq $code } | Select-Object -First 1
    $put = Invoke-Api PUT "/api/v1/methodologies/$mid/versions/$newId/rules/$code" (@{ matchJson = [string]$rule.matchJson; priority = [int]$rule.priority; isActive = $false } | ConvertTo-Json)
    if ($put.StatusCode -ne 200) { Stop-Step "retire rule $code of draft $newId HTTP $($put.StatusCode); draft left for review" $put.Content }
}
Write-Step "draft $newId ($next): $($retire.Count) rule row(s) of the removed clauses retired" @{ codes = $retire }

if ($ReviewerCredentialFile) { Connect-Api $ReviewerCredentialFile }
$reason = "c11-10 (P-1124-2): $Methodology $next = $ver without $($plan.Removed.Count) row clause(s) the category rule always rejected and with their $($retire.Count) rule row(s) retired (W15z borrowed rules; values unchanged)"
$pub = Invoke-Api POST "/api/v1/methodologies/$mid/versions/$newId/publish" (@{ changeReason = $reason; effectiveFrom = $from } | ConvertTo-Json)
if ($pub.StatusCode -ne 200) { Stop-Step "publish $next (draft $newId) HTTP $($pub.StatusCode); draft left for review" $pub.Content }
$after = @(Invoke-Sql "SELECT CONCAT(v.Status, '|', v.Engine, '|', LOWER(CONVERT(varchar(64), v.ScriptHash, 2)), '|', (SELECT COUNT(*) FROM calc.TestCase t WHERE t.MethodologyVersionId = v.Id), '|', (SELECT COUNT(*) FROM calc.MethodologyRule r WHERE r.MethodologyVersionId = v.Id AND r.IsActive = 0)) FROM calc.MethodologyVersion v WHERE v.Id = $newId")[0] -split '\|'
if ($after[0] -ne '1' -or $after[1] -ne '1' -or $after[2] -ne [string]$saved.hash) { Stop-Step "published $next (id $newId) is not Published/Script with the saved hash: $($after -join '|')" $null }
if ([int]$after[4] -lt $retire.Count) { Stop-Step "published $next (id $newId) has $($after[4]) inactive rule row(s), expected at least $($retire.Count)" $null }
[IO.File]::WriteAllText((Join-Path $Work 'row-narrowing-result.json'), (@{ methodology = $Methodology; fromVersionId = $vid; versionId = $newId; version = $next; effectiveFrom = $from; scriptHash = [string]$saved.hash; testCases = [int]$after[3]; removed = $plan.Removed.Count } | ConvertTo-Json), $utf8)
Write-Step "published $Methodology $next (id $newId) from $from, script hash $($saved.hash), golden cases $($after[3])" $null
