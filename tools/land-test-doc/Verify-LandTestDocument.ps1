<#
.SYNOPSIS
    Read-only acceptance check of the Land test document: per methodology >=1 result, 0 rejected rows, 0 errors.

.DESCRIPTION
    Runs verify-land-test-doc.sql (SELECT only) with sqlcmd -E against the given database and compares the Current
    calculation run of (document, period) with land-test-doc.methodologies.csv:
      * results    calc.CalculationResult rows of the methodology >= 1;
      * rejected   calc.CalculationIssue Kind = CategoryRuleRejected for the methodology or its row = 0
                   (on a build without that kind a rejected row shows up as results = 0);
      * unmatched  calc.CalculationIssue Kind = NoMatchingRule on an LTD- row = 0;
      * errors     latest run not Failed; failed formula steps (calc.CalculationStep.Value IS NULL) on the row = 0,
                   except methodologies marked knownConstantGap = yes (missing constants in AF data, human's
                   decision 11.11: listed as KNOWN, they do not block acceptance); knownReason names another
                   known cause (default "missing constants");
      * substance  no per-substance template result (OutputCode ...XXX...) without SubstanceEntryId.
    Note: failed steps are stored only for the run that computed the row (TraceLevel ErrorsOnly keeps no other steps,
    and carry-over copies results without steps). After a per-sheet recalculation the Current run computed one
    sheet only, so a row of another sheet shows 0 failed steps whatever happened to it. Then pass -Tables with the
    tables of the recalculated sheet and run the check after each sheet, before the next recalculation:
    rows of other tables get NOT-CHECKED. Without -Tables the run must be a whole-document recalculation.
.PARAMETER Tables       Table codes (e.g. T11_01,T11_02) recalculated by the Current run; empty = whole document.
    Exit 0 = accepted, 1 = not accepted, 2 = document/run not found.

.PARAMETER Server       SQL Server instance (default localhost). Integrated security.
.PARAMETER Database     Database to read (no default), e.g. EcrScratch_MB.
.PARAMETER DocumentId   Document id; or -Marker to find it by its English name in -ProjectId.
.PARAMETER Marker       Default LAND-TEST-ALL-METHODOLOGIES.
.PARAMETER ProjectId    Default 1 (LANDUI2026).
.PARAMETER PeriodKey    Default 202609.

.EXAMPLE
    .\Verify-LandTestDocument.ps1 -Database EcrScratch_MB -PeriodKey 202609
#>
[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [Parameter(Mandatory)][string]$Database,
    [int]$DocumentId = 0,
    [string]$Marker = 'LAND-TEST-ALL-METHODOLOGIES',
    [int]$ProjectId = 1,
    [int]$PeriodKey = 202609,
    [string]$MethodologyFile,
    [string[]]$Tables = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($MethodologyFile)) { $MethodologyFile = Join-Path $here 'land-test-doc.methodologies.csv' }
$sqlFile = Join-Path $here 'verify-land-test-doc.sql'
if ($Database -notmatch '^[A-Za-z0-9_]+$') { throw "Unexpected database name '$Database'." }
$plan = @(Import-Csv -LiteralPath $MethodologyFile -Encoding UTF8)
# powershell -File passes "T11_01,T11_02" as one string.
$Tables = @($Tables | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknownTables = @($Tables | Where-Object { @($plan | ForEach-Object table) -notcontains $_ })
if ($unknownTables.Count -gt 0) { throw "Tables not in the plan: $($unknownTables -join ', ')." }

if ($DocumentId -le 0) {
    $safe = $Marker.Replace("'", "''")
    $q = "SET NOCOUNT ON; SELECT CAST(Id AS nvarchar(20)) FROM doc.Document WHERE ProjectId = $ProjectId AND JSON_VALUE(NameL10n, '$.en') = N'$safe';"
    $ids = @(& sqlcmd -S $Server -E -d $Database -b -f 65001 -W -h -1 -Q $q | Where-Object { $_ -match '^\s*\d+\s*$' } | ForEach-Object { [int]$_.Trim() })
    if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed ($LASTEXITCODE) looking up the document." }
    if ($ids.Count -ne 1) { Write-Host "Document '$Marker' in project $ProjectId`: found $($ids.Count). Pass -DocumentId."; exit 2 }
    $DocumentId = $ids[0]
}

$lines = @(& sqlcmd -S $Server -E -d $Database -b -f 65001 -W -h -1 -s "`t" -v DocumentId=$DocumentId PeriodKey=$PeriodKey -i $sqlFile)
if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed ($LASTEXITCODE): $($lines -join ' ')" }
$facts = @($lines | Where-Object { $_ -match "^(RUN|CURRENT|RESULT|ISSUE|FAILEDSTEP|MASKED|NOSUBST)`t" } | ForEach-Object {
        $a = $_.Split("`t")
        [pscustomobject]@{ Section = $a[0]; Key = $a[1]; Methodology = $(if ($a.Count -gt 2) { $a[2] } else { '' }); Value = $(if ($a.Count -gt 3) { $a[3] } else { '' }) }
    })

$run = @($facts | Where-Object Section -eq 'RUN') | Select-Object -First 1
$current = @($facts | Where-Object Section -eq 'CURRENT') | Select-Object -First 1
if ($null -eq $run -or $null -eq $current -or [string]::IsNullOrWhiteSpace($current.Key)) {
    Write-Host "Document $DocumentId period $PeriodKey has no Current calculation run. Run the recalculation first."
    exit 2
}
Write-Host "Document $DocumentId, period $PeriodKey; latest run $($run.Key) status $($run.Methodology); Current run $($current.Key)."
if ($Tables.Count -eq 0) { Write-Host "WARNING: assumes run $($current.Key) is a WHOLE-document recalculation. After a per-sheet recalculation pass -Tables." }

$fail = 0
$known = New-Object System.Collections.Generic.List[string]
$report = New-Object System.Collections.Generic.List[object]
foreach ($m in $plan) {
    $rowKey = 'LTD-' + $m.row
    $results = 0
    foreach ($f in @($facts | Where-Object { $_.Section -eq 'RESULT' -and $_.Methodology -eq $m.methodology })) { $results += [int]$f.Value }
    $rejected = @($facts | Where-Object { $_.Section -eq 'ISSUE' -and $_.Value -eq 'CategoryRuleRejected' -and ($_.Methodology -eq $m.methodology -or $_.Key -eq $rowKey) }).Count
    $unmatched = @($facts | Where-Object { $_.Section -eq 'ISSUE' -and $_.Value -eq 'NoMatchingRule' -and $_.Key -eq $rowKey }).Count
    $failed = 0
    foreach ($f in @($facts | Where-Object { $_.Section -eq 'FAILEDSTEP' -and $_.Key -eq $rowKey })) { $failed += [int]$f.Value }
    if ($Tables.Count -gt 0 -and $Tables -notcontains $m.table) {
        $report.Add([pscustomobject]@{ Methodology = $m.methodology; Row = $rowKey; Table = $m.table; Results = $results; Expected = $m.expectedResults; Rejected = ''; Unmatched = ''; FailedSteps = ''; Verdict = 'NOT-CHECKED'; Why = 'table not in -Tables (results carried, failed steps not kept)' })
        continue
    }
    $isKnown = ($m.knownConstantGap -eq 'yes')
    $reason = 'missing constants'
    if ($m.PSObject.Properties['knownReason'] -and -not [string]::IsNullOrWhiteSpace($m.knownReason)) { $reason = $m.knownReason }
    $verdict = 'OK'
    $why = @()
    if ($results -lt 1) { $why += 'no results' }
    if ($rejected -gt 0) { $why += "rejected $rejected" }
    if ($unmatched -gt 0) { $why += "NoMatchingRule $unmatched" }
    if ($failed -gt 0 -and -not $isKnown) { $why += "failed steps $failed" }
    $noSubst = 0
    foreach ($f in @($facts | Where-Object { $_.Section -eq 'NOSUBST' -and $_.Key -eq $rowKey -and $_.Methodology -eq $m.methodology })) { $noSubst += [int]$f.Value }
    if ($noSubst -gt 0) { $why += "results without substance $noSubst" }
    if ($why.Count -gt 0) { $verdict = 'FAIL'; $fail++ }
    elseif ($failed -gt 0) { $verdict = 'KNOWN'; $known.Add("$($m.methodology) ($rowKey): $failed failed step(s) - $reason") }
    $report.Add([pscustomobject]@{ Methodology = $m.methodology; Row = $rowKey; Table = $m.table; Results = $results; Expected = $m.expectedResults; Rejected = $rejected; Unmatched = $unmatched; FailedSteps = $failed; Verdict = $verdict; Why = ($why -join '; ') })
}
$otherUnmatched = @($facts | Where-Object { $_.Section -eq 'ISSUE' -and $_.Value -eq 'NoMatchingRule' -and $_.Key -like 'LTD-*' -and ($plan | ForEach-Object { 'LTD-' + $_.row }) -notcontains $_.Key })
$report | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
if ($run.Methodology -eq 'Failed') { Write-Host "Latest run $($run.Key) FAILED: $($run.Value)"; $fail++ }
if ($otherUnmatched.Count -gt 0) { Write-Host "NoMatchingRule on rows outside the plan: $(($otherUnmatched | ForEach-Object Key) -join ', ')"; $fail++ }
if ($known.Count -gt 0) { Write-Host 'KNOWN (does not block acceptance):'; $known | ForEach-Object { Write-Host "  $_" } }
$failCount = @($report | Where-Object Verdict -eq 'FAIL').Count
$skipped = @($report | Where-Object Verdict -eq 'NOT-CHECKED').Count
$scope = $(if ($Tables.Count -gt 0) { 'SHEET ' } else { '' })
$state = $(if ($fail -gt 0) { 'NOT ACCEPTED' } elseif ($skipped -gt 0) { 'PASSED (these tables only)' } else { 'ACCEPTED' })
Write-Host ("{0}VERDICT: {1} - {2} methodologies, {3} OK/KNOWN, {4} FAIL, {5} NOT-CHECKED." -f $scope, $state, $plan.Count, ($plan.Count - $failCount - $skipped), $failCount, $skipped)
if ($fail -gt 0) { exit 1 }
exit 0
