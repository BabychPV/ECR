<#
.SYNOPSIS
    Loads the Excel permit books (docs/source/Permits: Air, Water, Waste) into the Permits module of an ECR stand through
    the public REST API: permit types (create -> replace -> publish), permits (Draft), rows (entries/batch, 500 per chunk,
    every chunk dry-run before the first write), activation. Customer-question defaults: tools/permits/README.md.
.DESCRIPTION
    SAFETY
      * Default is a DRY RUN: nothing is written. With -BaseUrl and credentials the stand is only READ (types, permits).
        Without -BaseUrl the plan is built from the workbooks alone.
      * -Execute writes. It needs -BaseUrl (local), -ConfirmStand (must match the stand: host:port or URL) and -Database
        (the database name; the confirmation becomes "<host:port>/<database>", see tools/land/EcrApi.ps1).
      * Logs carry COUNTERS only: no cell values, no permit data. Permit numbers are printed only with -ShowNumbers; the run
        file (see -LogDir) keeps numbers and ids, never row values.
    PER PERMIT (all-or-nothing)
      create Draft -> dry-run ALL chunks -> write chunks -> compare row counts -> activate. Any failure deletes the Draft
      (rows go with it), so a permit is either complete and Active or absent.
    IDEMPOTENCY KEY: (type, number, ValidFrom). Existing Active/Expired -> skipped (row count compared and reported);
      existing Draft with rows -> reported as PARTIAL, never merged; empty Draft -> reported, and loaded into (rows + activation) only with -ResumeEmptyDrafts.
    UNDO: -Rollback <runId> deletes only the Draft permits (and never-published types) that run created. Active permits and
      published types cannot be deleted through the API: restore the database backup (see README).
    EXIT CODES: 0 ok; 1 failure or invalid data; 2 warnings only (partial / row-count mismatch found); 3 guard / usage error.
.PARAMETER BaseUrl           local API address (or -Server host:port)
.PARAMETER ConfirmStand      stand confirmation (host:port or URL); mandatory for -Execute / -Rollback -Execute
.PARAMETER Database          database name of the stand; mandatory for -Execute (confirmation becomes <host:port>/<database>)
.PARAMETER User / Password   ECR account (or ECR_USER / ECR_PASSWORD)
.PARAMETER SourceDir         folder with the three books (default: docs/source/Permits of this or the main checkout)
.PARAMETER Books             book file names (relative to SourceDir) or full paths
.PARAMETER Only              type codes: AIR, WATER_DISCHARGE, WATER_INTAKE, WATER_GENERAL, WASTE
.PARAMETER Execute           write (after a clean plan); -DryRun wins
.PARAMETER NormalizeDashUnderscore  '_YYYY_YYYY' tail of a number -> '_YYYY-YYYY' (default: numbers are kept as in the book)
.PARAMETER ExcludeNumberPatterns    regex list of numbers that are NOT permits (service sheets, AF_ ids, permit_00N)
.PARAMETER ExcludeTestRows  default $true; -TestMarkers rows (qwwe, Test_Water) are dropped and COUNTED
.PARAMETER ServiceEntriesFile  CSV (number;location;validFrom;validTo) of Land-dictionary-only entries (permit_001.., AF_<guid>), loaded as AIR permits without rows
.PARAMETER ServiceValidFrom/ServiceValidTo  dates for service entries that have none in the file (never defaulted: no dates -> skipped)
.PARAMETER NoActivate        leave the loaded permits Draft (review / rollback scenario)
.PARAMETER RunId             id of this run (default: timestamp); the run file is permits-run-<RunId>.json in -LogDir
#>
[CmdletBinding()]
param(
    [string]$BaseUrl,
    [string]$Server,
    [string]$ConfirmStand,
    [string]$Database,
    [string]$User,
    [securestring]$Password,
    [string]$SourceDir,
    [string[]]$Books = @('PermitsInfo.Year.xlsx', 'PermitsInfoWater.Year.xlsx', 'PermitsInfoWaste.Year.xlsx'),
    [string[]]$Only = @(),
    [switch]$Execute,
    [switch]$DryRun,
    [switch]$NormalizeDashUnderscore,
    [string[]]$ExcludeNumberPatterns = @(),
    [bool]$ExcludeTestRows = $true,
    [string[]]$TestMarkers = @('qwwe', 'Test_Water'),
    [bool]$SkipEmptyPermits = $false,
    [string]$ServiceEntriesFile,
    [string]$ServiceLocation = 'service: Land dictionary only',
    [string]$ServiceValidFrom,
    [string]$ServiceValidTo,
    [ValidateSet('Air', 'None')][string]$GeneralDatesFrom = 'Air',
    [ValidateSet('IfDiffers', 'None')][string]$ActualEndMode = 'IfDiffers',
    [int]$ValidToAddDays = 1,
    [int]$ChunkSize = 500,
    [switch]$NoActivate,
    [switch]$ResumeEmptyDrafts,
    [string]$RunId,
    [string]$LogDir,
    [string]$Rollback,
    [switch]$ShowNumbers
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Permits.Excel.Lib.ps1')
. (Join-Path $here '..\land\EcrApi.ps1')
Set-StrictMode -Off

$exec = ($Execute.IsPresent -and -not $DryRun.IsPresent)
$script:ExitCode = 0
$script:Failed = 0
$script:Warned = 0
$script:RunStart = Get-Date

function Write-Log {
    param([string]$Text)
    Write-Host ((Get-Date).ToString('HH:mm:ss') + ' ' + $Text)
}

function Set-Exit {
    param([int]$Code)
    # 1 (failure) outranks 2 (warning); 3 (guard) outranks both
    if ($Code -eq 3 -or ($Code -eq 1 -and $script:ExitCode -ne 3) -or ($Code -eq 2 -and $script:ExitCode -eq 0)) { $script:ExitCode = $Code }
}

function Get-Prop {
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($null -eq $p) { return $null }
    return $p.Value
}

function Format-Failure {
    # HTTP status + machine codes only; never the response text (it may echo values)
    param($Response)
    $code = Get-EcrErrorCode $Response
    $key = Get-Prop $Response.Json 'messageKey'
    return ('HTTP {0} {1} {2}' -f $Response.Status, $code, $key).Trim()
}

function Invoke-EcrRetry {
    # one place for transient handling: 429 / 503 are retried with a pause; everything else is returned as is
    param([string]$Method, [string]$Path, $Body = $null)
    $attempt = 0
    while ($true) {
        $attempt++
        $r = Invoke-Ecr -Method $Method -Path $Path -Body $Body
        if (($r.Status -eq 429 -or $r.Status -eq 503) -and $attempt -lt 6) {
            $wait = 5 * $attempt
            $ra = $null
            if ($r.Headers.ContainsKey('Retry-After')) { $ra = 0; [void][int]::TryParse([string]$r.Headers['Retry-After'], [ref]$ra); if ($ra -gt 0) { $wait = [Math]::Min($ra, 60) } }
            Write-Log "  HTTP $($r.Status), retry $attempt in ${wait}s"
            Start-Sleep -Seconds $wait
            continue
        }
        return $r
    }
}

# ------------------------------------------------------------------ run file

if ([string]::IsNullOrWhiteSpace($RunId)) { $RunId = (Get-Date).ToString('yyyyMMdd-HHmmss') }
if ([string]::IsNullOrWhiteSpace($LogDir)) { $LogDir = Join-Path ([IO.Path]::GetTempPath()) 'ecr-permits-load' }
$script:Run = [ordered]@{ runId = $RunId; startedAt = $script:RunStart.ToString('o'); baseUrl = $null; database = $Database; execute = $exec; typesCreated = @(); permits = @() }

function Save-Run {
    if (-not $exec) { return }
    if (-not (Test-Path -LiteralPath $LogDir)) { [void](New-Item -ItemType Directory -Path $LogDir -Force) }
    $file = Join-Path $LogDir "permits-run-$RunId.json"
    $json = $script:Run | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($file, $json, (New-Object System.Text.UTF8Encoding($false)))
}

function Set-RunPermit {
    param([string]$Type, [string]$Number, [string]$ValidFrom, $Id, [string]$State, [int]$Rows)
    $found = $null
    foreach ($p in $script:Run.permits) { if ($p.type -eq $Type -and $p.number -eq $Number -and $p.validFrom -eq $ValidFrom) { $found = $p } }
    if ($null -eq $found) {
        $found = [ordered]@{ type = $Type; number = $Number; validFrom = $ValidFrom; id = $Id; state = $State; rows = $Rows }
        $script:Run.permits = @($script:Run.permits) + @($found)
    }
    else { $found.id = $Id; $found.state = $State; $found.rows = $Rows }
    Save-Run
}

# ------------------------------------------------------------------ stand session

function Resolve-StandUrl {
    if ([string]::IsNullOrWhiteSpace($BaseUrl) -and -not [string]::IsNullOrWhiteSpace($Server)) { return 'http://' + $Server.Trim() }
    return $BaseUrl
}

$standUrl = Resolve-StandUrl
$script:Run.baseUrl = $standUrl
$haveCreds = (-not [string]::IsNullOrWhiteSpace($User)) -or (-not [string]::IsNullOrWhiteSpace($env:ECR_USER))

if (($exec -or -not [string]::IsNullOrWhiteSpace($Rollback)) -and $exec) {
    if ([string]::IsNullOrWhiteSpace($standUrl)) { Write-Log 'GUARD: -Execute needs -BaseUrl (or -Server).'; exit 3 }
    if ([string]::IsNullOrWhiteSpace($Database)) { Write-Log 'GUARD: -Execute needs -Database (the confirmation is <host:port>/<database>).'; exit 3 }
    if (-not $haveCreds) { Write-Log 'GUARD: -Execute needs -User/-Password (or ECR_USER / ECR_PASSWORD).'; exit 3 }
    try { Set-EcrStandConfirmation -Stand $ConfirmStand -Url $standUrl -Database $Database } catch { Write-Log ('GUARD: ' + $_.Exception.Message); exit 3 }
    if ($ChunkSize -lt 1 -or $ChunkSize -gt 2000) { Write-Log 'GUARD: -ChunkSize 1..2000.'; exit 3 }
}

$standOnline = $false
function Connect-Stand {
    if ([string]::IsNullOrWhiteSpace($standUrl) -or -not $haveCreds) { return $false }
    Assert-LocalBaseUrl $standUrl
    Connect-Ecr -BaseUrl $standUrl -User $User -Password $Password
    return $true
}

# ------------------------------------------------------------------ rollback

if (-not [string]::IsNullOrWhiteSpace($Rollback)) {
    $file = Join-Path $LogDir "permits-run-$Rollback.json"
    if (-not (Test-Path -LiteralPath $file)) { Write-Log "Rollback: run file for '$Rollback' not found in the log folder."; exit 3 }
    $run = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $standOnline) { $standOnline = Connect-Stand }
    if (-not $standOnline) { Write-Log 'Rollback needs -BaseUrl and credentials.'; exit 3 }
    if ($exec -and -not [string]::IsNullOrWhiteSpace((Get-Prop $run 'baseUrl')) -and ((Get-Prop $run 'baseUrl').TrimEnd('/') -ne $standUrl.TrimEnd('/'))) {
        Write-Log 'GUARD: the run file belongs to another stand address.'; exit 3
    }
    $del = 0; $kept = 0; $gone = 0; $fail = 0
    foreach ($p in @($run.permits)) {
        if ($null -eq $p.id) { continue }
        $g = Invoke-EcrRetry GET ("/api/v1/permits/{0}" -f $p.id)
        if ($g.Status -eq 404) { $gone++; continue }
        if ($g.Status -ne 200) { $fail++; Write-Log ('  permit read failed: ' + (Format-Failure $g)); continue }
        if ($g.Json.status -ne 'Draft') { $kept++; continue }
        if (-not $exec) { $del++; continue }
        $d = Invoke-EcrRetry DELETE ("/api/v1/permits/{0}" -f $p.id)
        if ($d.Status -eq 204) { $del++ } else { $fail++; Write-Log ('  delete failed: ' + (Format-Failure $d)) }
    }
    $tdel = 0; $tkept = 0
    foreach ($tc in @($run.typesCreated)) {
        $t = Invoke-EcrRetry GET ("/api/v1/permit-types/{0}" -f $tc)
        if ($t.Status -ne 200) { continue }
        if ($t.Json.status -eq 'Draft' -and $t.Json.version -le 1 -and $null -eq (Get-Prop $t.Json 'publishedAt')) {
            if ($exec) { $d = Invoke-EcrRetry DELETE ("/api/v1/permit-types/{0}" -f $tc); if ($d.Status -eq 204) { $tdel++ } else { $fail++ } } else { $tdel++ }
        }
        else { $tkept++ }
    }
    $mode = if ($exec) { 'EXECUTE' } else { 'DRY-RUN' }
    Write-Log "Rollback[$Rollback] ${mode}: draft permits deleted=$del, kept (not Draft)=$kept, already gone=$gone, draft types deleted=$tdel, published types kept=$tkept, failures=$fail"
    if ($kept -gt 0 -or $tkept -gt 0) { Write-Log 'Active permits / published types are not deletable via the API: restore the database backup to undo them.' }
    if ($fail -gt 0) { exit 1 }
    exit 0
}

# ------------------------------------------------------------------ plan (files only)

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $cands = @((Join-Path $here '..\..\docs\source\Permits'))
    $common = $null
    try { $common = (& git -C $here rev-parse --git-common-dir 2>$null) } catch { $common = $null }
    if ($common) { $cd = [string]$common; if (-not [IO.Path]::IsPathRooted($cd)) { $cd = Join-Path $here $cd }; $cands += (Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($cd))) 'docs\source\Permits') }
    foreach ($c in $cands) { if (Test-Path -LiteralPath $c) { $SourceDir = (Resolve-Path -LiteralPath $c).ProviderPath; break } }
}
if ([string]::IsNullOrWhiteSpace($SourceDir) -or -not (Test-Path -LiteralPath $SourceDir)) { Write-Log 'GUARD: -SourceDir is not set / not found (docs/source is not in git).'; exit 3 }

$opts = Get-DefaultLoadOptions
$opts.DashUnderscoreEquivalence = $NormalizeDashUnderscore.IsPresent
$opts.ExcludeNumberPatterns = $ExcludeNumberPatterns
$opts.ExcludeTestRows = $ExcludeTestRows
$opts.TestMarkers = $TestMarkers
$opts.GeneralDatesFrom = $GeneralDatesFrom
$opts.ActualEndMode = $ActualEndMode
$opts.ValidToAddDays = $ValidToAddDays
$opts.ServiceLocation = $ServiceLocation
$opts.ServiceValidFrom = $ServiceValidFrom
$opts.ServiceValidTo = $ServiceValidTo
$svc = @(Read-ServiceEntries -Path $ServiceEntriesFile)

Write-Log ("Load-Permits run={0} mode={1} books={2} only=[{3}]" -f $RunId, $(if ($exec) { 'EXECUTE' } else { 'DRY-RUN' }), @($Books).Count, (@($Only) -join ','))
$sw = [Diagnostics.Stopwatch]::StartNew()
$plan = Build-LoadPlan -SourceDir $SourceDir -Books $Books -Only $Only -Options $opts -SkipEmptyPermits $SkipEmptyPermits -ServiceEntries $svc
Write-Log ("Books read in {0:N1}s: {1} sheets-as-permits" -f $sw.Elapsed.TotalSeconds, $plan.Plans.Count)

$labels = @{}
$typeIndex = @{}
foreach ($p in $plan.Plans) {
    if (-not $typeIndex.ContainsKey($p.TypeCode)) { $typeIndex[$p.TypeCode] = 0 }
    $typeIndex[$p.TypeCode]++
    $labels[$p] = ('{0}#{1:00}' -f $p.TypeCode, $typeIndex[$p.TypeCode])
}
function Get-Label { param($P) if ($ShowNumbers) { return ('{0}[{1}]' -f $labels[$P], $P.Number) } return $labels[$P] }

$summary = @()
foreach ($code in $plan.Wanted) {
    $mine = @($plan.Plans | Where-Object { $_.TypeCode -eq $code })
    $ready = @($mine | Where-Object { $_.Status -eq 'Ready' })
    $rows = 0; foreach ($p in $ready) { $rows += $p.Rows.Count }
    $test = 0; foreach ($p in $mine) { $test += $p.TestExcluded }
    $summary += [pscustomobject]@{
        Type = $code; Sheets = $mine.Count; Ready = $ready.Count
        Excluded = @($mine | Where-Object { $_.Status -eq 'Excluded' }).Count
        Skipped = @($mine | Where-Object { $_.Status -eq 'Skipped' }).Count
        Invalid = @($mine | Where-Object { $_.Status -eq 'Invalid' }).Count
        Rows = $rows; TestRowsExcluded = $test
    }
}
Write-Log 'PLAN (from the workbooks):'
($summary | Format-Table -AutoSize | Out-String -Width 200).TrimEnd() -split "`r?`n" | ForEach-Object { Write-Host $_ }
$tot = [pscustomobject]@{
    Ready = ($summary | Measure-Object Ready -Sum).Sum; Rows = ($summary | Measure-Object Rows -Sum).Sum
    Test = ($summary | Measure-Object TestRowsExcluded -Sum).Sum; Invalid = ($summary | Measure-Object Invalid -Sum).Sum
}
Write-Log ("PLAN TOTAL: permits ready={0} rows={1} testRowsExcluded={2} invalid={3}" -f $tot.Ready, $tot.Rows, $tot.Test, $tot.Invalid)

foreach ($p in $plan.Plans) {
    if ($p.Status -eq 'Invalid') {
        $rej = ($p.Rejected.Keys | Sort-Object | ForEach-Object { '{0}={1}' -f $_, $p.Rejected[$_] }) -join ','
        Write-Log ("  INVALID {0}: reason={1} rejected=[{2}]" -f (Get-Label $p), $p.Reason, $rej)
        $script:Failed++
    }
    elseif (@($p.Notes).Count -gt 0 -and $p.Status -eq 'Ready') { Write-Log ("  note {0}: {1}" -f (Get-Label $p), (@($p.Notes) -join ';')) }
    if ($p.Status -eq 'Skipped') { Write-Log ("  skipped {0}: {1}" -f (Get-Label $p), $p.Reason) }
}
$maxlens = @{}
foreach ($p in $plan.Plans) { foreach ($k in $p.MaxLen.Keys) { $key = "$($p.TypeCode).$k"; if (-not $maxlens.ContainsKey($key) -or $p.MaxLen[$k] -gt $maxlens[$key]) { $maxlens[$key] = $p.MaxLen[$k] } } }
$longKeys = @($maxlens.Keys | Sort-Object | Where-Object { $maxlens[$_] -ge 150 } | ForEach-Object { '{0}={1}' -f $_, $maxlens[$_] })
if ($longKeys.Count -gt 0) { Write-Log ('  longest values (chars, >=150): ' + ($longKeys -join ', ')) }
if ($script:Failed -gt 0) { Set-Exit 1 }

# ------------------------------------------------------------------ stand read / write

function Get-AllPages {
    param([string]$Path)
    $items = New-Object System.Collections.ArrayList
    $cursor = $null
    do {
        $sep = if ($Path.Contains('?')) { '&' } else { '?' }
        $url = $Path + $sep + 'limit=500'
        if ($cursor) { $url += '&cursor=' + [uri]::EscapeDataString($cursor) }
        $r = Invoke-EcrRetry GET $url
        if ($r.Status -ne 200) { throw ("GET {0}: {1}" -f $Path.Split('?')[0], (Format-Failure $r)) }
        foreach ($i in @($r.Json.items)) { [void]$items.Add($i) }
        $cursor = Get-Prop $r.Json 'nextCursor'
    } while ($cursor)
    # plain array (callers wrap in @()): ",$items would nest the list in one element and member access would enumerate it
    return $items.ToArray()
}

function Test-TypeMatchesConfig {
    # published type must contain our table and every column (code, key, dataType)
    param($Dto, $Config)
    $tbl = @($Dto.tables) | Where-Object { $_.code -ieq $Config.TableCode } | Select-Object -First 1
    if ($null -eq $tbl) { return 'table-missing' }
    foreach ($spec in $Config.Columns) {
        $c = @($tbl.columns) | Where-Object { $_.code -ieq $spec.Code } | Select-Object -First 1
        if ($null -eq $c) { return 'column-missing' }
        if ($c.dataType -ne $spec.Type) { return 'column-type' }
        if ([bool]$c.key -ne [bool]$spec.Key) { return 'column-key' }
    }
    return $null
}

function Sync-PermitType {
    <#
    .SYNOPSIS
        Returns 'exists' | 'created' | 'would-create' | 'mismatch:<reason>' | 'error:<detail>'.
    #>
    param($Config)
    $code = $Config.Code
    $g = Invoke-EcrRetry GET ("/api/v1/permit-types/{0}" -f $code)
    if ($g.Status -eq 200) {
        $dto = $g.Json
        if ($dto.status -eq 'Published' -and -not [bool]$dto.hasUnpublishedChanges) {
            $why = Test-TypeMatchesConfig $dto $Config
            if ($why) { return "mismatch:$why" }
            return 'exists'
        }
        if ($dto.status -eq 'Published') { return 'mismatch:unpublished-changes' }
        if (-not $exec) { return 'would-publish-draft' }
        $rv = $dto.rowVersion
        $res = 'published-existing-draft'
    }
    elseif ($g.Status -eq 404) {
        if (-not $exec) { return 'would-create' }
        $content = ConvertTo-TypeContent $Config
        $c = Invoke-EcrRetry POST '/api/v1/permit-types' ([ordered]@{ code = $code; nameL10n = $content.nameL10n; category = $content.category })
        if ($c.Status -ne 201) { return 'error:create ' + (Format-Failure $c) }
        $script:Run.typesCreated = @($script:Run.typesCreated) + @($code)
        Save-Run
        $rv = $c.Json.rowVersion
        $res = 'created'
    }
    else { return 'error:read ' + (Format-Failure $g) }

    $content = ConvertTo-TypeContent $Config
    $put = Invoke-EcrRetry PUT ("/api/v1/permit-types/{0}" -f $code) ([ordered]@{ rowVersion = $rv; nameL10n = $content.nameL10n; category = $content.category; tables = $content.tables; rules = $content.rules })
    if ($put.Status -ne 200) { return 'error:replace ' + (Format-Failure $put) }
    $pub = Invoke-EcrRetry POST ("/api/v1/permit-types/{0}/publish" -f $code) ([ordered]@{ reason = 'Load of permit workbooks (Load-Permits)'; rowVersion = $put.Json.rowVersion })
    if ($pub.Status -ne 200) { return 'error:publish ' + (Format-Failure $pub) }
    return $res
}

function Get-BatchErrorSummary {
    param($Result)
    $agg = @{}
    foreach ($row in @($Result.rows)) {
        foreach ($e in @($row.errors)) {
            $k = '{0}|{1}|{2}' -f (Get-Prop $e 'field'), (Get-Prop $e 'errorCode'), (Get-Prop $e 'messageKey')
            if ($agg.ContainsKey($k)) { $agg[$k]++ } else { $agg[$k] = 1 }
        }
    }
    return ($agg.Keys | Sort-Object | ForEach-Object { '{0} x{1}' -f $_, $agg[$_] }) -join '; '
}

function Remove-DraftPermit {
    param($Id)
    $d = Invoke-EcrRetry DELETE ("/api/v1/permits/{0}" -f $Id)
    return ($d.Status -eq 204)
}

function Import-PermitPlan {
    <#
    .SYNOPSIS
        Creates ONE permit with its rows. Returns @{ Outcome; Rows; Detail } where Outcome is
        created | activated | failed. On failure the Draft is deleted (all-or-nothing).
    #>
    param($Plan, $Config, $ExistingDraftId = $null)
    $from = ConvertTo-IsoDate $Plan.ValidFrom
    $body = [ordered]@{
        typeCode = $Plan.TypeCode; number = $Plan.Number; location = $Plan.Location
        issueDate = (ConvertTo-IsoDate $Plan.IssueDate); validFrom = $from; validTo = (ConvertTo-IsoDate $Plan.ValidTo); reportingFlags = $null
    }
    if ($null -ne $ExistingDraftId) {
        # -ResumeEmptyDrafts: the empty Draft already exists; read it (rowVersion) instead of creating
        $c = Invoke-EcrRetry GET ("/api/v1/permits/{0}" -f $ExistingDraftId)
        if ($c.Status -ne 200) { return @{ Outcome = 'failed'; Rows = 0; Detail = ('read draft ' + (Format-Failure $c)) } }
    }
    else {
        $c = Invoke-EcrRetry POST '/api/v1/permits' $body
        if ($c.Status -ne 201) { return @{ Outcome = 'failed'; Rows = 0; Detail = ('create ' + (Format-Failure $c)) } }
    }
    $id = [long]$c.Json.id
    Set-RunPermit $Plan.TypeCode $Plan.Number $from $id 'draft' 0

    $fail = {
        param($why)
        $removed = Remove-DraftPermit $id
        Set-RunPermit $Plan.TypeCode $Plan.Number $from $(if ($removed) { $null } else { $id }) $(if ($removed) { 'removed' } else { 'draft-left' }) 0
        return @{ Outcome = 'failed'; Rows = 0; Detail = ($why + $(if ($removed) { ' (draft removed)' } else { ' (DRAFT LEFT: use -Rollback)' })) }
    }

    if ($null -ne $Plan.ActualEnd) {
        $put = Invoke-EcrRetry PUT ("/api/v1/permits/{0}" -f $id) ([ordered]@{
                rowVersion = $c.Json.rowVersion; number = $Plan.Number; location = $Plan.Location; issueDate = (ConvertTo-IsoDate $Plan.IssueDate)
                validFrom = $from; validTo = (ConvertTo-IsoDate $Plan.ValidTo); actualEnd = (ConvertTo-IsoDate $Plan.ActualEnd); reportingFlags = $null })
        if ($put.Status -ne 200) { return (& $fail ('actual-end ' + (Format-Failure $put))) }
    }

    $registry = 'PRM_{0}_{1}' -f $Plan.TypeCode, $Config.TableCode
    $items = ConvertTo-BatchItems -Rows $Plan.Rows -PermitId $id
    $chunks = Split-IntoChunks -Items $items -Size $ChunkSize

    # 1) dry-run ALL chunks before the first real write
    $n = 0
    foreach ($ch in $chunks) {
        $n++
        $r = Invoke-EcrRetry POST ("/api/v1/registries/{0}/entries/batch?dryRun=true" -f $registry) ([ordered]@{ items = @($ch) })
        if ($r.Status -ne 200) { return (& $fail ("dry-run chunk $n " + (Format-Failure $r))) }
        $errs = Get-BatchErrorSummary $r.Json
        if ($errs) { return (& $fail ("dry-run chunk $n rejected: " + $errs)) }
        $sum = [int]$r.Json.added + [int]$r.Json.updated + [int]$r.Json.unchanged
        if ($sum -ne $ch.Count) { return (& $fail ("dry-run chunk $n counted $sum of $($ch.Count)")) }
    }
    # 2) real chunks
    $written = 0
    $n = 0
    foreach ($ch in $chunks) {
        $n++
        $r = Invoke-EcrRetry POST ("/api/v1/registries/{0}/entries/batch?dryRun=false" -f $registry) ([ordered]@{ items = @($ch) })
        if ($r.Status -ne 200) { return (& $fail ("write chunk $n " + (Format-Failure $r))) }
        if (-not [bool]$r.Json.applied) { return (& $fail ("write chunk $n not applied: " + (Get-BatchErrorSummary $r.Json))) }
        $written += [int]$r.Json.added
    }
    if ($written -ne $Plan.Rows.Count) { return (& $fail "written $written of $($Plan.Rows.Count)") }
    # 3) server-side row count
    $v = Invoke-EcrRetry GET ("/api/v1/permits/{0}" -f $id)
    if ($v.Status -ne 200) { return (& $fail ('verify ' + (Format-Failure $v))) }
    $serverRows = 0
    foreach ($t in @($v.Json.tables)) { $serverRows += [int]$t.rowCount }
    if ($serverRows -ne $Plan.Rows.Count) { return (& $fail "row count on the server $serverRows differs from $($Plan.Rows.Count)") }
    Set-RunPermit $Plan.TypeCode $Plan.Number $from $id 'loaded' $serverRows
    if ($NoActivate) { return @{ Outcome = 'created'; Rows = $serverRows; Detail = 'left Draft (-NoActivate)' } }
    # 4) activation (overlap with an active permit of the same number -> 409; then nothing stays behind)
    $a = Invoke-EcrRetry POST ("/api/v1/permits/{0}/activate" -f $id)
    if ($a.Status -ne 200) { return (& $fail ('activate ' + (Format-Failure $a))) }
    Set-RunPermit $Plan.TypeCode $Plan.Number $from $id 'active' $serverRows
    return @{ Outcome = 'activated'; Rows = $serverRows; Detail = '' }
}

# ------------------------------------------------------------------ main

$standOnline = Connect-Stand
if (-not $standOnline) {
    Write-Log 'No stand access (no -BaseUrl/credentials): plan only; the stand was not read.'
    $final = if ($script:ExitCode -ne 0) { $script:ExitCode } else { 0 }
    Write-Log ("DONE (offline plan) exit={0}" -f $final)
    exit $final
}
Write-Log ("Stand read access ok ({0}); mode={1}" -f $(if ($exec) { $standUrl.Split('/')[2] + '/' + $Database } else { 'read only' }), $(if ($exec) { 'EXECUTE' } else { 'DRY-RUN' }))

$counts = [ordered]@{}
function Add-Count { param([string]$Type, [string]$Key, [int]$N = 1) if (-not $counts.Contains($Type)) { $counts[$Type] = [ordered]@{} }; if ($counts[$Type].Contains($Key)) { $counts[$Type][$Key] += $N } else { $counts[$Type][$Key] = $N } }

foreach ($code in $plan.Wanted) {
    $cfg = $plan.Types[$code]
    $ready = @($plan.Plans | Where-Object { $_.TypeCode -eq $code -and $_.Status -eq 'Ready' })
    if ($ready.Count -eq 0) { continue }
    $typeState = Sync-PermitType $cfg
    Write-Log ("TYPE {0}: {1}" -f $code, $typeState)
    if ($typeState -like 'mismatch:*' -or $typeState -like 'error:*') {
        Add-Count $code 'failed' $ready.Count
        $script:Failed += $ready.Count
        Set-Exit 1
        continue
    }
    $existing = @(Get-AllPages ("/api/v1/permits?type={0}" -f $code))
    foreach ($p in $ready) {
        $from = ConvertTo-IsoDate $p.ValidFrom
        $key = Get-IdempotencyKey $p.TypeCode $p.Number $from
        $hit = $null
        foreach ($e in $existing) {
            $ef = if ($e.validFrom) { [string]$e.validFrom } else { '' }
            if ((Get-IdempotencyKey $code ([string]$e.number) $ef.Substring(0, [Math]::Min(10, $ef.Length))) -eq $key) { $hit = $e; break }
        }
        $label = Get-Label $p
        $resumeId = $null
        if ($null -ne $hit -and $hit.status -eq 'Draft' -and [int]$hit.rowCount -eq 0 -and $ResumeEmptyDrafts) {
            Write-Log ("  {0}: resuming the empty Draft (-ResumeEmptyDrafts)" -f $label)
            $resumeId = [long]$hit.id
            $hit = $null
        }
        if ($null -ne $hit) {
            $rc = [int]$hit.rowCount
            if ($hit.status -eq 'Draft') {
                if ($rc -gt 0) {
                    Write-Log ("  {0}: PARTIAL existing Draft (rows={1}, planned={2}); not merged, use -Rollback or the UI" -f $label, $rc, $p.Rows.Count)
                    Add-Count $code 'partial'; Set-Exit 2; $script:Warned++; continue
                }
                Write-Log ("  {0}: existing EMPTY Draft; not touched (use -ResumeEmptyDrafts)" -f $label)
                Add-Count $code 'partial'; Set-Exit 2; $script:Warned++; continue
            }
            $note = ''
            if ($rc -ne $p.Rows.Count) { $note = " ROWCOUNT-MISMATCH server=$rc planned=$($p.Rows.Count)"; Set-Exit 2; $script:Warned++ }
            Write-Log ("  {0}: exists ({1}), skipped, rows={2}{3}" -f $label, $hit.status, $rc, $note)
            Add-Count $code 'skipped-existing'
            continue
        }
        if (-not $exec) {
            Write-Log ("  {0}: would create, rows={1}, chunks={2}, validFrom/To ok" -f $label, $p.Rows.Count, (Split-IntoChunks -Items $p.Rows -Size $ChunkSize).Count)
            Add-Count $code 'would-create'
            Add-Count $code 'rows' $p.Rows.Count
            continue
        }
        $sw2 = [Diagnostics.Stopwatch]::StartNew()
        $res = Import-PermitPlan $p $cfg $resumeId
        if ($res.Outcome -eq 'failed') {
            Write-Log ("  {0}: FAILED {1}" -f $label, $res.Detail)
            Add-Count $code 'failed'; $script:Failed++; Set-Exit 1
        }
        else {
            Write-Log ("  {0}: {1} rows={2} in {3:N1}s {4}" -f $label, $res.Outcome, $res.Rows, $sw2.Elapsed.TotalSeconds, $res.Detail)
            Add-Count $code $res.Outcome
            Add-Count $code 'rows' $res.Rows
        }
    }
}

Write-Log 'RESULT:'
foreach ($t in $counts.Keys) { Write-Log ("  {0}: {1}" -f $t, (($counts[$t].Keys | ForEach-Object { '{0}={1}' -f $_, $counts[$t][$_] }) -join ' ')) }
Write-Log ("DONE in {0:N1}s exit={1} runId={2}{3}" -f ((Get-Date) - $script:RunStart).TotalSeconds, $script:ExitCode, $RunId, $(if ($exec) { " runFile=" + (Join-Path $LogDir "permits-run-$RunId.json") } else { '' }))
exit $script:ExitCode
