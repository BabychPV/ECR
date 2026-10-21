<#
.SYNOPSIS
    TEMPLATE (analysis only): plan the move of the Land document header field `Permit` from the dictionary LAND_PERMIT to the
    Permits header registry PRM_AIR, as an Id -> Id map matched 1:1 by permit number.
.DESCRIPTION
    READ-ONLY. Connects with ApplicationIntent=ReadOnly and only SELECTs. -Execute is NOT implemented on purpose (see TODO below).
    Prints COUNTERS only: no document names, no field values; permit numbers/ids only with -ShowCodes. Not meant to be run until the
    PRM_AIR registry is loaded (Load-Permits.ps1) and the human has answered the TODO questions.

    What it does
      1. Source: entries of the registry -SourceRegistry (default LAND_PERMIT) and how many documents reference each
         (doc.DocumentHeaderValue.ValueRegistryEntryId, header fields named -HeaderFieldCode, all template versions).
      2. Target: entries of -TargetRegistry (default PRM_AIR) with their Permit_Number (EAV: dic.RegistryValue of the field
         -NumberField), active flag and validity window.
      3. Match by number: exact, then (with -NormalizeDashUnderscore, default on, matching only) ignoring '-' / '_'
         (e.g. _2023_2030 in Land vs _2023-2030 in Excel). 1:1 or nothing.
      4. Counters before / after; per source entry: docs, match status. Exit 1 if a source entry that has documents has no 1:1 match.
      5. -MapOut writes the map (oldId, oldCode, docs, newId, status) as JSON: identifiers only, no field values.

    TODO / questions that must be answered BEFORE any write is implemented (do not guess):
      * Q1 The field definition: the published template versions keep HeaderFieldDef.LookupRegistryDefId = LAND_PERMIT. The API header
        PATCH refuses a value from another registry (ECR-HDR-4223 foreignRegistry), so values cannot just be repointed in place.
        Needs a NEW template version with the field pointing to PRM_AIR (+ document migration to it), or a decision to change the
        definition of the published version. Which?
      * Q2 Approved / closed documents: may the header of an approved, submitted or closed-period document change? Periods of the
        affected documents are Scheduled/Open here (no Closed/Grace), but the sheet statuses (Draft/Submitted/Approved) are not read
        by this template. Is an edit of the header in a non-Draft sheet allowed, and does it need Reopen?
      * Q3 Validity window: the header Lookup check (ECR-HDR-4223) wants the target entry valid for at least one day of the period
        window. Permits ValidFrom/ValidTo come from Excel (some end mid-year); a document period outside the window would be refused.
        The script only counts this (windowOverlap), the rule for old periods is a decision.
      * Q4 Audit trail: a direct SQL UPDATE bypasses the header audit/history and the recalculation; the API route does not.
        Which route is acceptable?
      * Q5 Recalculation: Land formulas read only the permit number; confirm that no recalculation is needed after repointing.
.PARAMETER Server          SQL Server (default localhost); must be local (Assert-LocalSqlConnection)
.PARAMETER Database        database name (EcrLand* only)
.PARAMETER SourceRegistry / TargetRegistry / HeaderFieldCode / NumberField
.PARAMETER MapOut          JSON file with the Id -> Id map
.PARAMETER MapFormat       legacy (default) | lookupRemaps (body part of POST /documents/{id}/migrate-version, Analiz plan MIGRATE-LOOKUP-REMAP)
.PARAMETER Execute         NOT IMPLEMENTED: throws and lists the TODO questions
#>
[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [Parameter(Mandatory)][string]$Database,
    [string]$SourceRegistry = 'LAND_PERMIT',
    [string]$TargetRegistry = 'PRM_AIR',
    [string]$HeaderFieldCode = 'Permit',
    [string]$NumberField = 'Permit_Number',
    [bool]$NormalizeDashUnderscore = $true,
    [string]$MapOut,
    [ValidateSet('legacy', 'lookupRemaps')][string]$MapFormat = 'legacy',
    [switch]$ShowCodes,
    [switch]$Execute
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here '..\land\EcrApi.ps1')
. (Join-Path $here 'Permits.Excel.Lib.ps1')
Set-StrictMode -Off

if ($Execute) {
    throw 'Remap-HeaderPermit -Execute is not implemented: see the TODO questions Q1..Q5 in the script header. This build only plans (read-only).'
}

$conn = "Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true;ApplicationIntent=ReadOnly"
[void](Assert-LocalSqlConnection $conn)

function Invoke-Select {
    param($Cn, [string]$Sql, [hashtable]$Params = @{})
    $cmd = $Cn.CreateCommand(); $cmd.CommandText = $Sql; $cmd.CommandTimeout = 120
    foreach ($k in $Params.Keys) { [void]$cmd.Parameters.AddWithValue('@' + $k, $Params[$k]) }
    $rd = $cmd.ExecuteReader(); $rows = New-Object System.Collections.ArrayList
    while ($rd.Read()) { $o = [ordered]@{}; for ($i = 0; $i -lt $rd.FieldCount; $i++) { $o[$rd.GetName($i)] = $(if ($rd.IsDBNull($i)) { $null } else { $rd[$i] }) }; [void]$rows.Add([pscustomobject]$o) }
    $rd.Close(); return ,$rows
}

$cn = New-Object System.Data.SqlClient.SqlConnection $conn
$cn.Open()
try {
    $src = Invoke-Select $cn @"
SELECT e.Id, e.Code, e.IsActive, e.IsDeleted,
       (SELECT COUNT(*) FROM doc.DocumentHeaderValue v JOIN cfg.HeaderFieldDef h ON h.Id = v.HeaderFieldDefId
         WHERE h.Code = @field AND v.ValueRegistryEntryId = e.Id) AS Docs
FROM dic.RegistryEntry e JOIN cfg.RegistryDef d ON d.Id = e.RegistryDefId WHERE d.Code = @reg ORDER BY e.Id
"@ @{ reg = $SourceRegistry; field = $HeaderFieldCode }
    $versions = Invoke-Select $cn "SELECT COUNT(DISTINCT h.TemplateVersionId) AS V, COUNT(*) AS Vals FROM doc.DocumentHeaderValue v JOIN cfg.HeaderFieldDef h ON h.Id = v.HeaderFieldDefId WHERE h.Code = @field AND v.ValueRegistryEntryId IS NOT NULL" @{ field = $HeaderFieldCode }
    $tgtReg = Invoke-Select $cn 'SELECT Id FROM cfg.RegistryDef WHERE Code = @reg' @{ reg = $TargetRegistry }
    $tgt = @()
    if ($tgtReg.Count -gt 0) {
        $tgt = Invoke-Select $cn @"
SELECT e.Id, e.IsActive, e.IsDeleted, e.ValidFrom, e.ValidTo, v.ValueString AS Num
FROM dic.RegistryEntry e
JOIN cfg.RegistryFieldDef f ON f.RegistryDefId = e.RegistryDefId AND f.Code = @num
JOIN dic.RegistryValue v ON v.RegistryEntryId = e.Id AND v.RegistryFieldDefId = f.Id
WHERE e.RegistryDefId = @rid AND e.IsDeleted = 0
"@ @{ rid = [int]$tgtReg[0].Id; num = $NumberField }
    }
}
finally { $cn.Close() }

$res = Resolve-HeaderPermitMap -Source $src -Target $tgt -Normalize $NormalizeDashUnderscore
$map = $res.Map; $counters = $res.Counters
$docsBefore = 0; foreach ($s in $src) { $docsBefore += [int]$s.Docs }
Write-Host ("Source {0}: entries={1}, entries with documents={2}, header values={3}, template versions with values={4}" -f $SourceRegistry, $src.Count, $counters.withDocs, $docsBefore, $versions[0].V)
if ($tgtReg.Count -eq 0) { Write-Host ("Target registry {0} does not exist yet (load Permits first): no matches can be planned." -f $TargetRegistry) }
else { Write-Host ("Target {0}: entries={1}" -f $TargetRegistry, $tgt.Count) }
Write-Host ("Match (1:1 by number): matched={0} noMatch={1} ambiguous={2} inactiveTarget={3} normalized={4}" -f $counters.matched, $counters.noMatch, $counters.ambiguous, $counters.inactiveTarget, $counters.normalized)
$usedMapped = @($map | Where-Object { $_.docs -gt 0 -and $null -ne $_.newId }).Count
$usedUnmapped = @($map | Where-Object { $_.docs -gt 0 -and $null -eq $_.newId }).Count
Write-Host ("Used entries (with documents): mapped={0} unmapped={1}" -f $usedMapped, $usedUnmapped)
Write-Host ("Documents: before (on {0})={1}; after (on {2}, planned)={3}; blocked={4}" -f $SourceRegistry, $docsBefore, $TargetRegistry, $counters.docsMapped, $counters.docsBlocked)
# ids only on the console; codes (permit numbers) never; with -ShowCodes they go to the map FILE only
foreach ($m in $map | Where-Object { $_.docs -gt 0 }) {
    Write-Host ("  source Id={0}: docs={1} status={2}{3}" -f $m.oldId, $m.docs, $m.status, $(if ($m.normalized) { ' normalized' } else { '' }))
}
if ($MapOut) {
    if ($MapFormat -eq 'lookupRemaps') {
        $obj = ConvertTo-LookupRemapsDocument -Map $map -Database $Database -HeaderFieldCode $HeaderFieldCode -IncludeCodes:$ShowCodes
    }
    else {
        $items = @($map | ForEach-Object { $m = [ordered]@{ oldId = $_.oldId }; if ($ShowCodes) { $m['oldCode'] = $_.oldCode }; $m['docs'] = $_.docs; $m['newId'] = $_.newId; $m['status'] = $_.status; $m })
        $obj = [ordered]@{ source = $SourceRegistry; target = $TargetRegistry; createdAt = (Get-Date).ToString('o'); map = $items }
    }
    $dir = Split-Path -Parent $MapOut
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
    [IO.File]::WriteAllText($MapOut, ($obj | ConvertTo-Json -Depth 8), (New-Object System.Text.UTF8Encoding($false)))
    Write-Host ("Map written ({0}); ids{1}, no field values." -f $MapFormat, $(if ($ShowCodes) { ' and codes' } else { '' }))
}
if ($counters.docsBlocked -gt 0 -or $usedUnmapped -gt 0) { Write-Host 'REFUSED: documents without a 1:1 target; fix the data first.'; exit 1 }
exit 0