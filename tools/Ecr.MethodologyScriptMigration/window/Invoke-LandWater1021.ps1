<#
.SYNOPSIS
    ✎ P-1124-2 (людина 24.11: одна дорога розрахунку, вода — методологією на ECR Script, без формули таблиці): крок вікна Land 1.0.21.0 —
    T7b_00.ECW_EC_tons_M01..M12 знову вихід ECW_C09_02_01 (Calculated + прив'язки), перенос документів 1.0.20.0 -> 1.0.21.0, перерахунок,
    звірка. Той самий скрипт для копії й live (live — лише з -LiveConfirmed).
.DESCRIPTION
    Порядок вікна: Invoke-MsMigrationPhase WaterV2 -> FinalizeProve -> Finalize -> ЦЕЙ скрипт: snapshot before -> builder -> verifytpl ->
    dryrun -> apply -> recalc -> snapshot after -> compare.
      account   : (лише копія) обліковий запис збірки f13-* з ролями LandOwner + TemplateAdministrator; пароль випадковий, лише у -AccountsFile.
      snapshot  : -Tag before|after — у -Out: (1) значення води T7b_00 ECW_EC_tons за (документ, період, рядок): before — обчислені комірки
                  формули таблиці (IsCalculated), after — результати ECW_C09_02_01 чинних прогонів; (2) усі ВВЕДЕНІ комірки документів
                  Land; (3) проблеми розрахунку за видом і документом; (4) результатів за документом; (5) висячі посилання результатів/знімків.
      builder   : Build-LandTemplate -Stage YearMonths,Template,Methodologies -YearMonthsTargetVersion 1.0.21.0 (клон останньої
                  опублікованої, зворотний крок Formula -> Calculated для води, прив'язки за моделлю, публікація).
      verifytpl : 1.0.21.0 опубліковано; T7b_00 ECW_EC_tons_M01..M12 — Calculated, без живих формул, 12 активних прив'язок ECW_C09_02_01.
      dryrun    : POST documents/{id}/migrate-version dryRun Safe для кожного документа LAND_UI на 1.0.20.0: canApply, lost 0, refusals 0.
      apply     : той самий перенос async (backupConfirmation = -Bak), чекає задачі.
      recalc    : POST documents/{id}/recalculate для відкритих періодів кожного перенесеного документа; чекає задачі.
      compare   : ворота: вода after = before для КОЖНОЇ комірки (0 розбіжностей); введені комірки тотожні; проблеми за видом на
                  документ не зросли; результатів не менше; висячих посилань 0.
#>
param(
    [Parameter(Mandatory)][ValidateSet('account', 'snapshot', 'builder', 'verifytpl', 'dryrun', 'apply', 'recalc', 'compare')][string]$Step,
    [Parameter(Mandatory)][string]$Database,
    [Parameter(Mandatory)][int]$Port,
    [Parameter(Mandatory)][string]$Out,
    [string]$AccountsFile,
    [ValidatePattern('^f13-[a-z0-9-]+$')][string]$User = 'f13-p11242-landbuilder',
    [string]$BuilderWt,
    [string]$SourceVersion = '1.0.20.0',
    [string]$TargetVersion = '1.0.21.0',
    [string]$Tag,
    [string]$Bak,
    [switch]$LiveConfirmed
)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if (-not $BuilderWt) { $BuilderWt = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path }
if ($Database -in 'ECR', 'FLERT', 'EcrLegacyProd') { throw "refused database $Database" }
if ($Database -eq 'EcrLandUi' -and -not $LiveConfirmed) { throw 'live (EcrLandUi) needs -LiveConfirmed' }
if ($Database -eq 'EcrLandUi' -and $Step -eq 'account') { throw 'account step is for copies only (live: the window account)' }
New-Item -ItemType Directory -Force $Out | Out-Null
$api = "http://localhost:$Port"
$log = Join-Path $Out 'land-water-1021.txt'
function L([string]$m) { $l = "$((Get-Date).ToString('HH:mm:ss')) [$Step] $m"; Add-Content -Path $log -Value $l -Encoding utf8; $l }
function Sql([string]$q, [int]$t = 900) {
    $f = Join-Path $Out 'q.sql'
    [IO.File]::WriteAllText($f, "SET NOCOUNT ON; IF DB_NAME() <> N'$Database' THROW 50000, 'wrong db', 1; $q", (New-Object Text.UTF8Encoding $false))
    $r = & sqlcmd -S localhost -E -d $Database -b -I -W -h -1 -s '|' -t $t -f i:65001 -i $f
    if ($LASTEXITCODE -ne 0) { throw "sql rc $LASTEXITCODE : $($r -join ' ')" }
    return @($r | Where-Object { $null -ne $_ -and $_.Trim() -ne '' })
}
function Call($m, $rel, $body, [int]$timeout = 1800) {
    $p = @{ UseBasicParsing = $true; Uri = "$api$rel"; Method = $m; WebSession = $script:ws; TimeoutSec = $timeout }
    if ($null -ne $body) { $p.ContentType = 'application/json; charset=utf-8'; $p.Body = [Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Compress -Depth 8)) }
    try { $r = Invoke-WebRequest @p; $b = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }; $code = [int]$r.StatusCode }
    catch { $resp = $_.Exception.Response; $b = ''; if ($resp) { $sr = New-Object IO.StreamReader($resp.GetResponseStream()); $b = $sr.ReadToEnd() }; $code = if ($resp) { [int]$resp.StatusCode } else { -1 } }
    [pscustomobject]@{ Code = $code; Body = $b }
}
function Login {
    if (-not $AccountsFile -or -not (Test-Path $AccountsFile)) { throw "no accounts file (run -Step account on a copy)" }
    $line = Get-Content $AccountsFile -TotalCount 1; $u, $pw = $line -split "`t", 2
    $script:ws = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $r = Call 'POST' '/api/v1/login/local' @{ userName = $u; password = $pw }; $pw = $null; $line = $null
    if ($r.Code -ne 200) { throw "login $($r.Code)" }
}
function VersionId([string]$v) {
    $x = @(Sql "SELECT v.Id FROM cfg.TemplateVersion v JOIN cfg.Template t ON t.Id = v.TemplateId WHERE t.Code = N'LAND_UI' AND v.Version = N'$v' AND v.Status = 1")
    if ($x.Count -eq 0) { throw "LAND_UI $v is not published" }; [int]$x[0]
}
function LandDocs([int]$tv) { @(Sql "SELECT d.Id FROM doc.Document d JOIN doc.Project p ON p.Id = d.ProjectId WHERE p.TemplateVersionId = $tv ORDER BY d.Id" | ForEach-Object { [long]$_ }) }
function WaitJobs([datetime]$since, [int]$maxSec = 3600) {
    $s = $since.ToUniversalTime().AddSeconds(-5).ToString('yyyy-MM-dd HH:mm:ss')
    foreach ($i in 1..($maxSec / 5)) { Start-Sleep 5; $n = [int]@(Sql "SELECT COUNT(*) FROM itg.JobProgress WITH (NOLOCK) WHERE State IN ('Queued','Running') AND CreatedAt >= '$s'")[0]; if ($n -eq 0 -and $i -ge 2) { break } }
    Sql "SELECT CONCAT(LEFT(JobId,90),' | ',State,' | ',LEFT(ISNULL(Error,''),200)) FROM itg.JobProgress WITH (NOLOCK) WHERE CreatedAt >= '$s' ORDER BY CreatedAt"
}

switch ($Step) {
    'account' {
        $rb = New-Object byte[] 18; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($rb); $pw = 'Aa1!' + ([Convert]::ToBase64String($rb) -replace '[+/=]', 'x')
        $salt = New-Object byte[] 16; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($salt)
        $kdf = New-Object Security.Cryptography.Rfc2898DeriveBytes($pw, $salt, 200000, [Security.Cryptography.HashAlgorithmName]::SHA512)
        $h = "1.200000.$([Convert]::ToBase64String($salt)).$([Convert]::ToBase64String($kdf.GetBytes(64)))"
        $o = Sql "SET XACT_ABORT ON; BEGIN TRAN; IF NOT EXISTS (SELECT 1 FROM sec.[User] WHERE UserName = N'$User') INSERT sec.[User] (UserName, DisplayName, Provider, PasswordHash, SecurityStamp, FailedAttempts, MustChangePassword, IsBootstrapAdmin, IsActive, CreatedAt) VALUES (N'$User', N'P-1124-2 Land builder (test copy)', 1, N'$h', CONVERT(nvarchar(64), NEWID()), 0, 0, 0, 1, SYSUTCDATETIME()); ELSE UPDATE sec.[User] SET PasswordHash = N'$h', IsActive = 1, FailedAttempts = 0, LockedUntil = NULL, MustChangePassword = 0 WHERE UserName = N'$User'; DECLARE @u int = (SELECT Id FROM sec.[User] WHERE UserName = N'$User'); INSERT sec.RoleAssignment (RoleId, UserId) SELECT r.Id, @u FROM sec.Role r WHERE r.Code IN (N'LandOwner', N'TemplateAdministrator') AND NOT EXISTS (SELECT 1 FROM sec.RoleAssignment x WHERE x.RoleId = r.Id AND x.UserId = @u); COMMIT; SELECT CONCAT(N'user ', @u, N' roles ', (SELECT COUNT(*) FROM sec.RoleAssignment WHERE UserId = @u));"
        $h = $null
        New-Item -ItemType Directory -Force (Split-Path $AccountsFile) | Out-Null
        Set-Content -Path $AccountsFile -Value "$User`t$pw" -Encoding ascii; $pw = $null
        L "$($o -join '') ; accounts file written (value not printed)"
    }
    'snapshot' {
        if ($Tag -notin 'before', 'after') { throw '-Tag before|after' }
        # (1) вода: before — обчислені комірки формули таблиці; after — результати методології чинних прогонів (місяць = місяць періоду).
        $water = if ($Tag -eq 'before') {
            "SELECT CONCAT(ti.DocumentId,'|',c.MonthNumber,'|',r.RowKey,'=',CONVERT(varchar(60),CONVERT(decimal(38,12),cv.ValueNumeric))) FROM doc.CellValue cv JOIN doc.TableRow r ON r.Id=cv.TableRowId AND r.PeriodKey=cv.PeriodKey JOIN doc.TableInstance ti ON ti.Id=r.TableInstanceId AND ti.PeriodKey=r.PeriodKey JOIN cfg.TableDef td ON td.Id=ti.TableDefId JOIN cfg.ColumnDef c ON c.Id=cv.ColumnDefId WHERE td.Code=N'T7b_00' AND c.Code LIKE N'ECW[_]EC[_]tons[_]M[01][0-9]' AND cv.ValueNumeric IS NOT NULL AND cv.IsEmpty=0 AND r.IsDeleted=0 AND ti.SheetCopyId IS NULL AND c.MonthNumber = ti.PeriodKey % 100"
        } else {
            "SELECT CONCAT(x.DocumentId,'|',x.PeriodKey % 100,'|',x.SourceRowKey,'=',CONVERT(varchar(60),CONVERT(decimal(38,12),x.Value))) FROM calc.CalculationResult x JOIN calc.CalculationRun run ON run.Id=x.CalculationRunId AND run.Status='Current' JOIN calc.MethodologyVersion v ON v.Id=x.MethodologyVersionId JOIN calc.Methodology m ON m.Id=v.MethodologyId WHERE m.Code=N'ECW_C09_02_01' AND x.Kind=0"
        }
        $w = @(Sql $water | Sort-Object)
        [IO.File]::WriteAllLines((Join-Path $Out "water-$Tag.tsv"), [string[]]$w, (New-Object Text.UTF8Encoding $false))
        # (2) введені комірки документів Land (місячні й не-місячні; без Formula/Calculated) — мусять пережити перенос.
        $inp = @(Sql "SELECT CONCAT(ti.DocumentId,'|',cv.PeriodKey,'|',td.Code,'|',r.RowKey,'|',c.Code,'=',ISNULL(CONVERT(varchar(64),HASHBYTES('SHA2_256',cv.ValueString),2),''),'#',ISNULL(CONVERT(varchar(60),cv.ValueNumeric),''),'#',ISNULL(CONVERT(varchar(30),cv.ValueDate,126),''),'#',ISNULL(CONVERT(varchar(5),cv.ValueBool),''),'#',ISNULL(CONVERT(varchar(12),cv.ValueRegistryEntryId),'')) FROM doc.CellValue cv JOIN doc.TableRow r ON r.Id=cv.TableRowId AND r.PeriodKey=cv.PeriodKey JOIN doc.TableInstance ti ON ti.Id=r.TableInstanceId AND ti.PeriodKey=r.PeriodKey JOIN cfg.ColumnDef c ON c.Id=cv.ColumnDefId JOIN cfg.TableDef td ON td.Id=ti.TableDefId JOIN doc.Document d ON d.Id=ti.DocumentId JOIN cfg.Template t ON t.Id=d.TemplateId WHERE t.Code=N'LAND_UI' AND cv.IsCalculated=0 AND c.DataType NOT IN (6,8) AND r.IsDeleted=0 AND cv.IsEmpty=0" 1800 | Sort-Object)
        [IO.File]::WriteAllLines((Join-Path $Out "inputs-$Tag.tsv"), [string[]]$inp, (New-Object Text.UTF8Encoding $false))
        # (3)-(5) проблеми за видом і документом, результати за документом, висячі посилання.
        # RowInactive води на T7b_00 (B66: підсумок рядків, відсіяних фільтром D-425, - рядок об'єму S3_9 без статусу; так само
        # було з V1 на шаблонах <= 1.0.19) - не проблема, а повідомлення: окремим списком notices, поза воротами «не зросло».
        $waterNotice = "i.Kind='RowInactive' AND i.MethodologyId IN (SELECT Id FROM calc.Methodology WHERE Code=N'ECW_C09_02_01') AND i.TableDefId IN (SELECT Id FROM cfg.TableDef WHERE Code=N'T7b_00')"
        $iss = @(Sql "SELECT CONCAT(i.DocumentId,'|',i.Kind,'=',COUNT(*)) FROM calc.CalculationIssue i JOIN calc.CalculationRun run ON run.Id=i.CalculationRunId AND run.Status='Current' WHERE NOT ($waterNotice) GROUP BY i.DocumentId, i.Kind ORDER BY i.DocumentId, i.Kind")
        $notices = @(Sql "SELECT CONCAT(i.DocumentId,'|',i.PeriodKey,'|T7b_00|RowInactive|',ISNULL(i.RowKey,''),'|',ISNULL(CONVERT(varchar(12),i.AffectedCount),'')) FROM calc.CalculationIssue i JOIN calc.CalculationRun run ON run.Id=i.CalculationRunId AND run.Status='Current' WHERE $waterNotice ORDER BY i.DocumentId, i.PeriodKey")
        $res = @(Sql "SELECT CONCAT(x.DocumentId,'=',COUNT(*)) FROM calc.CalculationResult x JOIN calc.CalculationRun run ON run.Id=x.CalculationRunId AND run.Status='Current' GROUP BY x.DocumentId ORDER BY x.DocumentId")
        $dang = [int]@(Sql "SELECT (SELECT COUNT(*) FROM calc.CalculationResult r WHERE NOT EXISTS (SELECT 1 FROM calc.MethodologyVersion v WHERE v.Id = r.MethodologyVersionId)) + (SELECT COUNT(*) FROM calc.SubmissionSnapshot s CROSS APPLY OPENJSON(s.MethodologyVersionsJson) j WHERE ISJSON(s.MethodologyVersionsJson) = 1 AND TRY_CAST(JSON_VALUE(j.value, '$.methodologyVersionId') AS int) IS NOT NULL AND NOT EXISTS (SELECT 1 FROM calc.MethodologyVersion v WHERE v.Id = TRY_CAST(JSON_VALUE(j.value, '$.methodologyVersionId') AS int)))")[0]
        $formulas = [int]@(Sql 'SELECT COUNT(*) FROM calc.MethodologyVersion WHERE Engine = 0')[0]
        $state = [ordered]@{ tag = $Tag; issues = $iss; notices = $notices; results = $res; dangling = $dang; formulaVersions = $formulas; waterCells = $w.Count; inputCells = $inp.Count }
        [IO.File]::WriteAllText((Join-Path $Out "state-$Tag.json"), ($state | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))
        L "snapshot $Tag : water cells $($w.Count) ; input cells $($inp.Count) ; issues [$($iss -join ' ')] ; results [$($res -join ' ')] ; dangling $dang ; formula versions $formulas"
    }
    'builder' {
        $b = Join-Path $BuilderWt 'tools\land\Build-LandTemplate.ps1'
        if (-not (Test-Path $b)) { throw "no builder $b" }
        $cs = "Server=localhost;Database=$Database;Integrated Security=true;TrustServerCertificate=True"
        $blog = Join-Path $Out "builder-$((Get-Date).ToString('HHmmss')).log"
        # -Command, а не -File: -Stage — масив (YearMonths,Template,Methodologies), -File передав би його одним рядком.
        $inner = "& '$b' -Stage YearMonths,Template,Methodologies -Layout YearWithMonthColumns -StructureMode Sql -SyncFormulaUnits -YearMonthsTargetVersion $TargetVersion -BaseUrl $api -ConfirmStand localhost:$Port/$Database -SqlConnection '$cs' -BuildUser $User -AccountsFile '$AccountsFile'"
        $args2 = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $inner)
        $p = Start-Process powershell.exe -ArgumentList $args2 -WorkingDirectory (Split-Path $b) -RedirectStandardOutput $blog -RedirectStandardError "$blog.err" -Wait -PassThru -NoNewWindow
        L "builder exit $($p.ExitCode) ; log $blog"
        L "LAND_UI: $((Sql "SELECT STRING_AGG(CONCAT(v.Id,':',v.Version,':',v.Status,':L',v.Layout),', ') WITHIN GROUP (ORDER BY v.Id) FROM cfg.TemplateVersion v JOIN cfg.Template t ON t.Id=v.TemplateId WHERE t.Code=N'LAND_UI' AND v.Id >= 22") -join '')"
        if ($p.ExitCode -ne 0) { exit 2 }
    }
    'verifytpl' {
        $tv = VersionId $TargetVersion
        $calc = [int]@(Sql "SELECT COUNT(*) FROM cfg.ColumnDef c JOIN cfg.TableDef td ON td.Id=c.TableDefId JOIN cfg.SheetDef s ON s.Id=td.SheetDefId WHERE s.TemplateVersionId=$tv AND td.Code=N'T7b_00' AND c.IsDeleted=0 AND c.Code LIKE N'ECW[_]EC[_]tons[_]M[01][0-9]' AND c.DataType=8")[0]
        $live = [int]@(Sql "SELECT COUNT(*) FROM cfg.FormulaDef f JOIN cfg.ColumnDef c ON c.Id=f.ColumnDefId JOIN cfg.TableDef td ON td.Id=c.TableDefId JOIN cfg.SheetDef s ON s.Id=td.SheetDefId WHERE s.TemplateVersionId=$tv AND td.Code=N'T7b_00' AND c.Code LIKE N'ECW[_]EC[_]tons[_]M[01][0-9]' AND f.IsDeleted=0")[0]
        $bind = [int]@(Sql "SELECT COUNT(*) FROM cfg.CalculationBinding b JOIN calc.Methodology m ON m.Id=b.MethodologyId JOIN cfg.ColumnDef c ON c.Id=b.ColumnDefId JOIN cfg.TableDef td ON td.Id=c.TableDefId JOIN cfg.SheetDef s ON s.Id=td.SheetDefId WHERE s.TemplateVersionId=$tv AND td.Code=N'T7b_00' AND b.IsActive=1 AND m.Code=N'ECW_C09_02_01' AND c.Code LIKE N'ECW[_]EC[_]tons[_]M[01][0-9]'")[0]
        $alias = [int]@(Sql "SELECT COUNT(*) FROM cfg.MonthInputAlias a JOIN cfg.TableDef td ON td.Id=a.TableDefId JOIN cfg.SheetDef s ON s.Id=td.SheetDefId WHERE s.TemplateVersionId=$tv AND td.Code=N'T7b_00' AND a.Alias IN (N'Concentration', N'Volume')")[0]
        L "LAND_UI $TargetVersion (tv$tv): ECW_EC_tons_Mnn Calculated $calc/12 ; live formulas $live (0) ; active bindings ECW_C09_02_01 $bind/12 ; month aliases Concentration/Volume $alias/24"
        if ($calc -ne 12 -or $live -ne 0 -or $bind -ne 12 -or $alias -ne 24) { L 'TEMPLATE GATE: FAIL'; exit 3 } else { L 'TEMPLATE GATE: PASS' }
    }
    'dryrun' {
        Login; $from = VersionId $SourceVersion; $tv = VersionId $TargetVersion
        $bad = 0
        foreach ($d in @(LandDocs $from)) {
            $x = Call 'POST' "/api/v1/documents/$d/migrate-version" @{ targetVersionId = $tv; dryRun = $true; mode = 'Safe' }
            [IO.File]::WriteAllText((Join-Path $Out "dryrun-$d.json"), $x.Body, (New-Object Text.UTF8Encoding $false))
            if ($x.Code -ne 200) { L "doc $d DRYRUN HTTP $($x.Code): $($x.Body.Substring(0, [Math]::Min(400, $x.Body.Length)))"; $bad++; continue }
            $b = $x.Body | ConvertFrom-Json
            L "doc $d : canApply $($b.canApply) ; refusals [$($b.refusals -join ',')] ; transferred $($b.transferredValues) ; lost $($b.lostValues) ; recomputed $(if ($null -ne $b.recomputedValues) { $b.recomputedValues } else { 'n/a (reported only for a year transition)' })"
            if (-not $b.canApply -or $b.lostValues -ne 0 -or @($b.refusals).Count -gt 0) { $bad++ }
        }
        if ($bad -gt 0) { L 'DRYRUN STOP'; exit 3 } else { L 'DRYRUN GO' }
    }
    'apply' {
        if (-not $Bak -or -not (Test-Path $Bak)) { throw '-Bak must be the verified backup taken before this step' }
        Login; $from = VersionId $SourceVersion; $tv = VersionId $TargetVersion; $t0 = Get-Date
        foreach ($d in @(LandDocs $from)) {
            $x = Call 'POST' "/api/v1/documents/$d/migrate-version" @{ targetVersionId = $tv; dryRun = $false; mode = 'Safe'; async = $true; backupConfirmation = $Bak }
            [IO.File]::WriteAllText((Join-Path $Out "apply-$d.json"), $x.Body, (New-Object Text.UTF8Encoding $false))
            L "doc $d apply HTTP $($x.Code)"
            if ($x.Code -ne 202 -and $x.Code -ne 200) { exit 2 }
        }
        $j = WaitJobs $t0; foreach ($l in $j) { L "  job: $l" }
        $left = @(LandDocs $from).Count
        if (@($j | Where-Object { $_ -match '\| (Failed|Cancelled) \|' }).Count -gt 0 -or $left -ne 0) { L "APPLY GATE: FAIL (documents left on $SourceVersion : $left)"; exit 4 } else { L 'APPLY GATE: PASS' }
    }
    'recalc' {
        Login; $tv = VersionId $TargetVersion; $t0 = Get-Date
        foreach ($d in @(LandDocs $tv)) {
            $ps = @(Sql "SELECT PeriodKey FROM doc.Period WHERE ProjectId=(SELECT ProjectId FROM doc.Document WHERE Id=$d) AND State IN (1,2) ORDER BY PeriodKey" | ForEach-Object { [int]$_ })
            foreach ($pk in $ps) { $r = Call 'POST' "/api/v1/documents/$d/recalculate" @{ periodKey = $pk }; if ($r.Code -ge 300) { L "doc $d recalc $pk HTTP $($r.Code) $($r.Body.Substring(0, [Math]::Min(200, $r.Body.Length)))"; exit 2 } }
            L "doc $d : recalculate [$($ps -join ',')]"
        }
        $j = WaitJobs $t0; foreach ($l in $j) { L "  job: $l" }
        if (@($j | Where-Object { $_ -match '\| (Failed|Cancelled) \|' }).Count -gt 0) { L 'RECALC GATE: FAIL'; exit 4 } else { L 'RECALC GATE: PASS' }
    }
    'compare' {
        $wb = @([IO.File]::ReadAllLines((Join-Path $Out 'water-before.tsv'))); $wa = @([IO.File]::ReadAllLines((Join-Path $Out 'water-after.tsv')))
        $mapB = @{}; foreach ($x in $wb) { $k, $v = $x -split '=', 2; $mapB[$k] = [decimal]$v }
        $mapA = @{}; foreach ($x in $wa) { $k, $v = $x -split '=', 2; $mapA[$k] = [decimal]$v }
        $diff = @(); foreach ($k in $mapB.Keys) { if (-not $mapA.ContainsKey($k)) { $diff += "missing after $k=$($mapB[$k])" } elseif ($mapA[$k] -ne $mapB[$k]) { $diff += "value $k before $($mapB[$k]) after $($mapA[$k])" } }
        foreach ($k in $mapA.Keys) { if (-not $mapB.ContainsKey($k)) { $diff += "extra after $k=$($mapA[$k])" } }
        [IO.File]::WriteAllLines((Join-Path $Out 'water-diff.txt'), [string[]]$diff, (New-Object Text.UTF8Encoding $false))
        $ib = @([IO.File]::ReadAllLines((Join-Path $Out 'inputs-before.tsv'))); $ia = @([IO.File]::ReadAllLines((Join-Path $Out 'inputs-after.tsv')))
        # Документ, перенесений на нову версію, лишає введені значення ті самі; рядок порівнюється без PeriodKey-власника не змінюється.
        $lostInputs = @(Compare-Object $ib $ia | Where-Object { $_.SideIndicator -eq '<=' } | ForEach-Object { $_.InputObject })
        $sb = Get-Content (Join-Path $Out 'state-before.json') -Raw | ConvertFrom-Json; $sa = Get-Content (Join-Path $Out 'state-after.json') -Raw | ConvertFrom-Json
        $ihb = @{}; foreach ($x in @($sb.issues)) { $k, $v = $x -split '=', 2; $ihb[$k] = [int]$v }
        $grown = @(); foreach ($x in @($sa.issues)) { $k, $v = $x -split '=', 2; if ([int]$v -gt [int]$ihb[$k]) { $grown += "$k $([int]$ihb[$k]) -> $v" } }
        $rb = @{}; foreach ($x in @($sb.results)) { $k, $v = $x -split '=', 2; $rb[$k] = [int]$v }
        $fewer = @(); foreach ($k in $rb.Keys) { $after = @($sa.results | Where-Object { $_ -like "$k=*" }); $n = if ($after.Count) { [int](($after[0] -split '=')[1]) } else { 0 }; if ($n -lt $rb[$k]) { $fewer += "doc $k $($rb[$k]) -> $n" } }
        L "water cells before $($wb.Count) after $($wa.Count) ; differences $($diff.Count) ; input cells before $($ib.Count) after $($ia.Count), lost $($lostInputs.Count) ; issues grown [$($grown -join '; ')] ; results fewer [$($fewer -join '; ')] ; dangling $($sa.dangling) ; formula versions $($sa.formulaVersions) ; water notices (T7b_00 RowInactive, not gated) before $(@($sb.PSObject.Properties['notices'] | ForEach-Object { $_.Value }).Count) after [$(@($sa.notices) -join '; ')]"
        if ($diff.Count -gt 0 -or $lostInputs.Count -gt 0 -or $grown.Count -gt 0 -or $fewer.Count -gt 0 -or [int]$sa.dangling -ne 0) { L 'COMPARE GATE: FAIL'; exit 5 } else { L 'COMPARE GATE: PASS' }
    }
}
