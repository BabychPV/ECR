<#
.SYNOPSIS
    Creates or converges ONE Land test document whose rows make every coverable Land methodology produce results.

.DESCRIPTION
    Data: land-test-doc.cells.csv (one line per cell: row, table, methodology, rule, column, kind, value, source).
    Every value must carry a source (workbook/sheet/cell, methodology rule or dictionary entry). A cell with an empty
    value or an empty source is MISSING: the script lists all missing cells and refuses to write (exit 2). Nothing is
    invented here.

    Public API only, as the operator's own session:
      * authentication = the operator's existing session cookie (default name ecr.auth), passed as
        -AuthCookie (SecureString) or env ECR_AUTH_COOKIE. The script never logs in, never creates accounts or
        roles, never prints the cookie;
      * GET /api/v1/me, /api/v1/documents?projectId&q, /api/v1/projects/{id}/document-template,
        /api/v1/documents/{id}/tables?periodKey, /api/v1/documents/{id}/tables/{instance},
        /api/v1/registries, /api/v1/registries/{code}/entries;
      * writes (only with -Execute and a matching -ConfirmStand): POST /api/v1/documents (create),
        PATCH /api/v1/documents/{id}/cells, DELETE .../rows/{rowKey} (-Prune), POST .../recalculate (-Recalculate).

    Idempotent: the document is found by its fixed name (-Marker, en/ru/kz). Rows carry fixed keys LTD-<row id>
    (LTD-A01 ... LTD-A43). A re-run patches only cells whose value differs and creates only missing rows; a second
    run on a converged document writes nothing. Rows with the LTD- prefix that are not in the data file are reported
    (removed only with -Prune).

    Without -Execute the script is read-only: it validates the data file, checks the session, locates the document and
    prints the plan (rows to create, cells to change).

.PARAMETER BaseUrl       API base address, e.g. http://localhost:5094 (no default).
.PARAMETER ProjectId     Land project id (LANDUI2026 = 1 on EcrLandUi and its copies).
.PARAMETER PeriodKey     Year*100+Month. Default 202609 (human's decision; composition data exists up to 2026-09).
.PARAMETER Marker        Fixed document name used to find the document again. Default LAND-TEST-ALL-METHODOLOGIES.
.PARAMETER DataFile      Cells CSV. Default: land-test-doc.cells.csv next to the script.
.PARAMETER AuthCookie    Value of the session cookie (SecureString). Default: env ECR_AUTH_COOKIE.
.PARAMETER CookieName    Session cookie name. Default ecr.auth (Auth:CookieName).
.PARAMETER ConfirmStand  Required for -Execute: the API authority (host:port) or full base URL of the target stand.
.PARAMETER Execute       Write (create document, rows, cells). Without it: plan only.
.PARAMETER Prune         With -Execute: delete LTD- rows that are not in the data file.
.PARAMETER Recalculate   With -Execute: run the methodology recalculation for the period and wait for the job.
.PARAMETER JobTimeoutMinutes  Recalculation wait limit (default 30).

.EXAMPLE
    $c = Read-Host 'ecr.auth cookie value' -AsSecureString
    .\Fill-LandTestDocument.ps1 -BaseUrl http://localhost:5094 -ProjectId 1 -AuthCookie $c
    .\Fill-LandTestDocument.ps1 -BaseUrl http://localhost:5094 -ProjectId 1 -AuthCookie $c -ConfirmStand localhost:5094 -Execute -Recalculate
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaseUrl,
    [Parameter(Mandatory)][int]$ProjectId,
    [int]$PeriodKey = 202609,
    [string]$Marker = 'LAND-TEST-ALL-METHODOLOGIES',
    [string]$DataFile,
    [securestring]$AuthCookie,
    [string]$CookieName = 'ecr.auth',
    [string]$ConfirmStand,
    [switch]$Execute,
    [switch]$Prune,
    [switch]$Recalculate,
    [int]$JobTimeoutMinutes = 30
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($DataFile)) { $DataFile = Join-Path $here 'land-test-doc.cells.csv' }
$RowKeyPrefix = 'LTD-'

# ---------------------------------------------------------------- data file
function Read-LandTestCells {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Data file not found: $Path" }
    $items = @(Import-Csv -LiteralPath $Path -Encoding UTF8)
    if ($items.Count -eq 0) { throw "Data file is empty: $Path" }
    foreach ($h in 'row', 'table', 'methodology', 'rule', 'column', 'kind', 'value', 'source') {
        if ($items[0].PSObject.Properties.Match($h).Count -eq 0) { throw "Data file has no column '$h'." }
    }
    return $items
}

function Test-LandTestCells {
    <# Returns the list of problems (missing value/source, unknown kind, duplicate cell). Empty list = complete. #>
    param([Parameter(Mandatory)]$Items)
    $problems = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    foreach ($c in $Items) {
        $key = "$($c.row)|$($c.column)"
        if ($seen.ContainsKey($key)) { $problems.Add("DUPLICATE $($c.row) $($c.column)") }
        $seen[$key] = 1
        if ($c.kind -notin 'lookup', 'text', 'number') { $problems.Add("BAD KIND $($c.row) $($c.column): '$($c.kind)'") }
        if ([string]::IsNullOrWhiteSpace($c.value)) { $problems.Add("MISSING VALUE $($c.row) $($c.table).$($c.column) [$($c.kind)] ($($c.methodology))") }
        elseif ([string]::IsNullOrWhiteSpace($c.source)) { $problems.Add("MISSING SOURCE $($c.row) $($c.table).$($c.column) = $($c.value)") }
        if ($c.kind -eq 'number' -and -not [string]::IsNullOrWhiteSpace($c.value)) {
            $d = 0.0
            if (-not [double]::TryParse($c.value, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) {
                $problems.Add("NOT A NUMBER $($c.row) $($c.column): '$($c.value)'")
            }
        }
    }
    return , $problems
}

# ---------------------------------------------------------------- http
$script:Client = $null
$script:Base = $null

function Initialize-Api {
    param([Parameter(Mandatory)][string]$Url, [Parameter(Mandatory)][string]$Cookie, [Parameter(Mandatory)][string]$Name)
    $u = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$u)) { throw "Invalid -BaseUrl: $Url" }
    if ($u.Scheme -eq 'http' -and -not $u.IsLoopback) { throw "Refusing to send the session cookie over plain http to a non-local host ($($u.Host)). Use https." }
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.UseCookies = $false
    $script:Client = New-Object System.Net.Http.HttpClient($handler)
    $script:Client.Timeout = [TimeSpan]::FromMinutes(10)
    [void]$script:Client.DefaultRequestHeaders.TryAddWithoutValidation('Cookie', "$Name=$Cookie")
    $script:Base = $Url.TrimEnd('/')
}

function Invoke-Api {
    <# Returns @{Status; Json; Text}; never throws on 4xx/5xx; retries 429 using Retry-After. #>
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, $Body = $null)
    if ($Method -ne 'GET' -and -not $script:WriteAllowed) { throw "Internal: write call $Method $Path without -Execute/-ConfirmStand." }
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $req = New-Object System.Net.Http.HttpRequestMessage([System.Net.Http.HttpMethod]::new($Method), ($script:Base + $Path))
        if ($null -ne $Body) {
            $json = $Body | ConvertTo-Json -Depth 12 -Compress
            $req.Content = New-Object System.Net.Http.StringContent($json, [Text.Encoding]::UTF8, 'application/json')
        }
        $resp = $script:Client.SendAsync($req).GetAwaiter().GetResult()
        try {
            $status = [int]$resp.StatusCode
            $text = [string]$resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $retry = 5
            if ($null -ne $resp.Headers.RetryAfter -and $null -ne $resp.Headers.RetryAfter.Delta) { $retry = [Math]::Max(1, [int]$resp.Headers.RetryAfter.Delta.Value.TotalSeconds) }
        }
        finally { $resp.Dispose(); $req.Dispose() }
        if ($status -eq 429) { Start-Sleep -Seconds $retry; continue }
        $parsed = $null
        if (-not [string]::IsNullOrWhiteSpace($text)) { try { $parsed = $text | ConvertFrom-Json } catch { $parsed = $null } }
        return [pscustomobject]@{ Status = $status; Json = $parsed; Text = $text }
    }
    throw "$Method $Path`: still 429 after 20 attempts."
}

function Assert-Api {
    param([Parameter(Mandatory)]$Response, [Parameter(Mandatory)][string]$What, [int[]]$Ok = @(200, 201, 202, 204))
    if ($Ok -notcontains $Response.Status) {
        $code = ''
        if ($null -ne $Response.Json -and $Response.Json.PSObject.Properties.Match('errorCode').Count -gt 0) { $code = [string]$Response.Json.errorCode }
        $detail = ''
        if ($null -ne $Response.Json -and $Response.Json.PSObject.Properties.Match('detail').Count -gt 0) { $detail = [string]$Response.Json.detail }
        throw "$What failed: HTTP $($Response.Status) $code $detail"
    }
}

function Get-Prop {
    param($Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties.Match($Name)
    if ($p.Count -eq 0) { return $null }
    return $p[0].Value
}

function Get-EnName {
    <# nameL10n arrives as {"en":..} or {"values":{"en":..}}. #>
    param($L10n)
    if ($null -eq $L10n) { return '' }
    $v = Get-Prop $L10n 'values'
    if ($null -ne $v) { return [string](Get-Prop $v 'en') }
    return [string](Get-Prop $L10n 'en')
}

# ---------------------------------------------------------------- values
function ConvertTo-Comparable {
    param($Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [double] -or $Value -is [decimal] -or $Value -is [int] -or $Value -is [long]) {
        return ([decimal]$Value).ToString('G29', [Globalization.CultureInfo]::InvariantCulture)
    }
    $s = [string]$Value
    $d = [decimal]0
    if ([decimal]::TryParse($s, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$d)) { return $d.ToString('G29', [Globalization.CultureInfo]::InvariantCulture) }
    return $s
}

function Get-CellCurrent {
    <# Current cell value from a slice row (cells keyed by column code); lookup cells may arrive as an id or as {id,...}. #>
    param($Row, [Parameter(Mandatory)][string]$Code)
    $cells = Get-Prop $Row 'cells'
    $v = Get-Prop $cells $Code
    if ($null -eq $v) { return '' }
    if ($v -is [System.Management.Automation.PSCustomObject]) {
        foreach ($n in 'value', 'id', 'registryEntryId', 'valueRegistryEntryId') {
            $x = Get-Prop $v $n
            if ($null -ne $x) { return (ConvertTo-Comparable $x) }
        }
        return ''
    }
    return (ConvertTo-Comparable $v)
}

# ================================================================= main
$cells = Read-LandTestCells -Path $DataFile
$problems = Test-LandTestCells -Items $cells
$rowIds = @($cells | Select-Object -ExpandProperty row -Unique)
$tables = @($cells | Select-Object -ExpandProperty table -Unique)
Write-Host "Data: $($cells.Count) cells, $($rowIds.Count) rows, $($tables.Count) tables, $(@($cells | Select-Object -ExpandProperty methodology -Unique).Count) methodologies; period $PeriodKey; marker '$Marker'."
if ($problems.Count -gt 0) {
    Write-Host "DATA INCOMPLETE: $($problems.Count) problem(s). No value is invented; supply each one with its source in $DataFile."
    $problems | ForEach-Object { Write-Host "  $_" }
}

$script:WriteAllowed = $false
$exec = $Execute.IsPresent
if ($exec) {
    if ($problems.Count -gt 0) { Write-Host 'Refusing to write: the data file is incomplete (see above).'; exit 2 }
    $u = [Uri]$BaseUrl
    $forms = @($BaseUrl.TrimEnd('/').ToLowerInvariant(), $u.Authority.ToLowerInvariant())
    if ([string]::IsNullOrWhiteSpace($ConfirmStand) -or $forms -notcontains $ConfirmStand.Trim().TrimEnd('/').ToLowerInvariant()) {
        throw "-Execute needs -ConfirmStand equal to '$($u.Authority)' or '$($BaseUrl.TrimEnd('/'))'."
    }
}

$plain = $null
if ($null -ne $AuthCookie) { $plain = (New-Object System.Net.NetworkCredential('', $AuthCookie)).Password }
elseif (-not [string]::IsNullOrEmpty($env:ECR_AUTH_COOKIE)) { $plain = $env:ECR_AUTH_COOKIE }
if ([string]::IsNullOrEmpty($plain)) {
    Write-Host "No session: pass -AuthCookie (SecureString) or set env ECR_AUTH_COOKIE to the value of the '$CookieName' cookie of an already signed-in operator. The script does not sign in."
    exit 3
}
Initialize-Api -Url $BaseUrl -Cookie $plain -Name $CookieName
$plain = $null

$me = Invoke-Api GET '/api/v1/me'
if ($me.Status -ne 200) { Write-Host "Session not accepted: GET /api/v1/me -> HTTP $($me.Status). Sign in again and pass the fresh cookie."; exit 3 }
Write-Host "Session OK (user id $(Get-Prop $me.Json 'id'))."

# ---- document by marker
$found = Invoke-Api GET ("/api/v1/documents?projectId=$ProjectId&limit=50&q=" + [Uri]::EscapeDataString($Marker))
Assert-Api $found 'GET documents'
$docs = @(@(Get-Prop $found.Json 'items') | Where-Object { $null -ne $_ -and (Get-EnName (Get-Prop $_ 'nameL10n')) -ceq $Marker })
if ($docs.Count -gt 1) { throw "More than one document named '$Marker' in project $ProjectId (ids $(($docs | ForEach-Object { $_.id }) -join ', ')). Rename the extra ones; nothing written." }

$template = Invoke-Api GET "/api/v1/projects/$ProjectId/document-template"
Assert-Api $template 'GET document-template'
$docId = $null
if ($docs.Count -eq 1) {
    $docId = [int]$docs[0].id
    Write-Host "Document found: id $docId, business key $(Get-Prop $docs[0] 'businessKey')."
}
elseif (-not $exec) {
    Write-Host "PLAN: document '$Marker' does not exist yet: it would be created with all sheets of template $(Get-Prop $template.Json 'templateCode') $(Get-Prop $template.Json 'version') and $($rowIds.Count) rows."
    Write-Host 'Plan only (no -Execute).'
    if ($problems.Count -gt 0) { exit 2 }
    exit 0
}
else {
    $script:WriteAllowed = $true
    $sheetIds = @(@(Get-Prop $template.Json 'sheets') | ForEach-Object { [int]$_.id })
    $body = @{ projectId = $ProjectId; sheetDefIds = $sheetIds; name = @{ en = $Marker; ru = $Marker; kz = $Marker }; templateVersionId = [int](Get-Prop $template.Json 'templateVersionId') }
    $created = Invoke-Api POST '/api/v1/documents' $body
    Assert-Api $created 'POST documents'
    $docId = [int](Get-Prop $created.Json 'documentId')
    Write-Host "Document created: id $docId ($($sheetIds.Count) sheets)."
}
$script:WriteAllowed = $exec

# ---- table instances of the period (created lazily by this GET)
$tl = Invoke-Api GET "/api/v1/documents/$docId/tables?periodKey=$PeriodKey"
Assert-Api $tl "GET tables period $PeriodKey"
$instances = @{}
foreach ($t in @($tl.Json)) { if ($null -ne $t) { $instances[[string]$t.tableCode] = $t } }
$absent = @($tables | Where-Object { -not $instances.ContainsKey($_) })
if ($absent.Count -gt 0) { throw "Tables not in document $docId for $PeriodKey`: $($absent -join ', ') (sheet not included or template differs). Nothing written." }

# ---- registries (lookup code -> entry id), as of the last day of the period
$asOf = [DateTime]::new([int][Math]::Floor($PeriodKey / 100), [int]($PeriodKey % 100), 1).AddMonths(1).AddDays(-1).ToString('yyyy-MM-dd')
$regList = Invoke-Api GET '/api/v1/registries'
Assert-Api $regList 'GET registries'
$regItems = Get-Prop $regList.Json 'items'
if ($null -eq $regItems) { $regItems = $regList.Json }
$regCodeById = @{}
foreach ($r in @($regItems)) { if ($null -ne $r) { $regCodeById[[string](Get-Prop $r 'id')] = [string](Get-Prop $r 'code') } }
$entryCache = @{}
function Resolve-EntryId {
    param([Parameter(Mandatory)][string]$RegistryCode, [Parameter(Mandatory)][string]$EntryCode)
    if (-not $entryCache.ContainsKey($RegistryCode)) {
        $e = Invoke-Api GET ("/api/v1/registries/" + [Uri]::EscapeDataString($RegistryCode) + "/entries?asOf=$asOf")
        Assert-Api $e "GET registries/$RegistryCode/entries"
        $map = @{}
        $list = Get-Prop $e.Json 'items'
        if ($null -eq $list) { $list = $e.Json }
        foreach ($x in @($list)) { if ($null -ne $x) { $map[[string]$x.code] = [int]$x.id } }
        $entryCache[$RegistryCode] = $map
    }
    $m = $entryCache[$RegistryCode]
    if (-not $m.ContainsKey($EntryCode)) { throw "Registry $RegistryCode has no active entry '$EntryCode' on $asOf." }
    return $m[$EntryCode]
}

# ---- per table: diff and patch
$totCreate = 0; $totCells = 0; $totUnchanged = 0; $stale = New-Object System.Collections.Generic.List[string]
$validationErrors = New-Object System.Collections.Generic.List[string]
foreach ($tableCode in $tables) {
    $inst = $instances[$tableCode]
    $instanceId = [long]$inst.tableInstanceId
    $slice = Invoke-Api GET "/api/v1/documents/$docId/tables/$instanceId"
    Assert-Api $slice "GET table $tableCode"
    $colByCode = @{}
    foreach ($c in @(Get-Prop $slice.Json 'columns')) { if ($null -ne $c) { $colByCode[[string]$c.code] = $c } }
    $rowByKey = @{}
    foreach ($r in @(Get-Prop $slice.Json 'rows')) { if ($null -ne $r) { $rowByKey[[string]$r.rowKey] = $r } }

    $patchRows = New-Object System.Collections.ArrayList
    $tableCells = @($cells | Where-Object { $_.table -eq $tableCode })
    foreach ($rid in @($tableCells | Select-Object -ExpandProperty row -Unique)) {
        $rowKey = $RowKeyPrefix + $rid
        $existing = $null
        if ($rowByKey.ContainsKey($rowKey)) { $existing = $rowByKey[$rowKey] }
        $changes = New-Object System.Collections.ArrayList
        foreach ($c in @($tableCells | Where-Object { $_.row -eq $rid })) {
            if (-not $colByCode.ContainsKey($c.column)) { throw "Column $tableCode.$($c.column) not in the template of document $docId." }
            $col = $colByCode[$c.column]
            if ([bool](Get-Prop $col 'isReadOnly')) { throw "Column $tableCode.$($c.column) is read-only." }
            if ([string]::IsNullOrWhiteSpace($c.value)) { continue }   # plan mode with incomplete data
            $desired = $null
            if ($c.kind -eq 'lookup') {
                $rd = [string](Get-Prop $col 'lookupRegistryDefId')
                if (-not $regCodeById.ContainsKey($rd)) { throw "Column $tableCode.$($c.column) is not a lookup column (registry def '$rd')." }
                $desired = Resolve-EntryId -RegistryCode $regCodeById[$rd] -EntryCode $c.value
            }
            elseif ($c.kind -eq 'number') { $desired = [decimal]::Parse($c.value, [Globalization.CultureInfo]::InvariantCulture) }
            else { $desired = [string]$c.value }
            $current = if ($null -ne $existing) { Get-CellCurrent -Row $existing -Code $c.column } else { '' }
            if ($current -ceq (ConvertTo-Comparable $desired)) { $totUnchanged++; continue }
            $v = $desired
            if ($c.kind -eq 'number') { $v = $desired.ToString([Globalization.CultureInfo]::InvariantCulture) }
            [void]$changes.Add(@{ columnCode = $c.column; value = $v })
        }
        if ($null -eq $existing) { $totCreate++ }
        if ($changes.Count -gt 0 -or $null -eq $existing) {
            # ⚠ Not $base: PowerShell names are case-insensitive, so $base would overwrite $script:Base (the API URL).
            $rowBaseVer = $null
            if ($null -ne $existing) { $rowBaseVer = [string](Get-Prop $existing 'rowVersion') }
            [void]$patchRows.Add(@{ rowKey = $rowKey; baseVersion = $rowBaseVer; cells = @($changes) })
            $totCells += $changes.Count
            Write-Host ("  {0} {1} {2}: {3} cell(s) to write" -f $tableCode, $rowKey, $(if ($null -eq $existing) { 'NEW' } else { 'existing' }), $changes.Count)
        }
    }
    $wanted = @($tableCells | ForEach-Object { $RowKeyPrefix + $_.row } | Select-Object -Unique)
    foreach ($k in @($rowByKey.Keys)) {
        if ($k.StartsWith($RowKeyPrefix) -and $wanted -notcontains $k) { $stale.Add("$tableCode/$k") }
    }
    if (-not $exec -or $patchRows.Count -eq 0) { continue }

    $patch = Invoke-Api PATCH "/api/v1/documents/$docId/cells" @{ tableInstanceId = $instanceId; periodKey = $PeriodKey; origin = 'UserEdit'; rows = @($patchRows) }
    Assert-Api $patch "PATCH cells $tableCode"
    foreach ($v in @(Get-Prop $patch.Json 'validation')) {
        if ($null -ne $v -and [string](Get-Prop $v 'severity') -match 'Error') { $validationErrors.Add("$tableCode $(Get-Prop $v 'rowKey').$(Get-Prop $v 'columnCode'): $(Get-Prop $v 'ruleCode') $(Get-Prop $v 'message')") }
    }
}

Write-Host "Rows to create: $totCreate; cells to write: $totCells; cells already converged: $totUnchanged."
if ($stale.Count -gt 0) {
    Write-Host "Rows with prefix $RowKeyPrefix not in the data file: $($stale -join ', ')"
    if ($exec -and $Prune) {
        foreach ($s in $stale) {
            $tc, $rk = $s.Split('/', 2)
            $del = Invoke-Api DELETE "/api/v1/documents/$docId/tables/$([long]$instances[$tc].tableInstanceId)/rows/$([Uri]::EscapeDataString($rk))"
            Assert-Api $del "DELETE row $s"
            Write-Host "  removed $s"
        }
    }
    elseif ($exec) { Write-Host '  (kept; re-run with -Prune to remove them: a spare row gives a NoMatchingRule diagnostic)' }
}
if ($validationErrors.Count -gt 0) { Write-Host "Validation errors after PATCH:"; $validationErrors | ForEach-Object { Write-Host "  $_" } }
if (-not $exec) {
    Write-Host 'Plan only (no -Execute).'
    if ($problems.Count -gt 0) { exit 2 }
    exit 0
}

function Wait-RecalcJob {
    <# Waits for one recalculation job; returns its final state. #>
    param([Parameter(Mandatory)][string]$JobId, [Parameter(Mandatory)][DateTime]$Deadline)
    $state = ''
    $job = $null
    while ((Get-Date) -lt $Deadline) {
        Start-Sleep -Seconds 3
        $job = Invoke-Api GET ('/api/v1/jobs/' + [Uri]::EscapeDataString($JobId))
        Assert-Api $job "GET job $JobId"
        $state = [string](Get-Prop $job.Json 'state')
        if ($state -in 'Succeeded', 'Failed', 'Cancelled') { break }
    }
    Write-Host "Recalculation job $JobId`: $state $(if ($null -ne $job) { Get-Prop $job.Json 'errorCode' })"
    return $state
}

if ($Recalculate) {
    $deadline = (Get-Date).AddMinutes($JobTimeoutMinutes)
    $post = Invoke-Api POST "/api/v1/documents/$docId/recalculate" @{ periodKey = $PeriodKey }
    if ($post.Status -eq 403) {
        # The whole-document recalculation needs rights this login may lack; sheet scope (Q-331) needs only the
        # sheet's. Sheets with data-file tables, one job at a time, at most 6 starts a minute (the endpoint quota).
        $sheets = @($tables | ForEach-Object { [int](Get-Prop $instances[$_] 'sheetDefId') } | Select-Object -Unique)
        Write-Host "Whole-document recalculation: HTTP 403. Falling back to $($sheets.Count) sheet(s), at most 6 a minute."
        $failed = 0
        $starts = New-Object System.Collections.Generic.List[DateTime]
        foreach ($sheetId in $sheets) {
            if ($starts.Count -ge 6) {
                $wait = 61 - ((Get-Date) - $starts[$starts.Count - 6]).TotalSeconds
                if ($wait -gt 0) { Start-Sleep -Seconds ([int][Math]::Ceiling($wait)) }
            }
            $starts.Add((Get-Date))
            $sp = Invoke-Api POST "/api/v1/documents/$docId/recalculate" @{ periodKey = $PeriodKey; sheetDefId = $sheetId }
            Assert-Api $sp "POST recalculate sheet $sheetId"
            $sj = [string](Get-Prop $sp.Json 'jobId')
            if ([string]::IsNullOrEmpty($sj)) { Write-Host "  sheet $sheetId`: nothing to recalculate."; continue }
            Write-Host "  sheet $sheetId`: job $sj started."
            if ((Wait-RecalcJob -JobId $sj -Deadline $deadline) -ne 'Succeeded') { $failed++ }
        }
        if ($failed -gt 0) { exit 1 }
    }
    else {
        Assert-Api $post 'POST recalculate'
        $jobId = [string](Get-Prop $post.Json 'jobId')
        Write-Host "Recalculation job $jobId started."
        if ((Wait-RecalcJob -JobId $jobId -Deadline $deadline) -ne 'Succeeded') { exit 1 }
    }
}
Write-Host "DOCUMENT_ID=$docId PERIOD=$PeriodKey. Next: Verify-LandTestDocument.ps1 -DocumentId $docId -PeriodKey $PeriodKey."
if ($validationErrors.Count -gt 0) { exit 1 }
exit 0
