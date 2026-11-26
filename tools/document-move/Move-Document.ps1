<#
.SYNOPSIS
    b14 document-move (D-577, P-1125-ONEPROJ): one-off move of documents into another project of the same year
    (live: HSE400 98 and HSE401 93 -> LANDUI2026). Modes: PreImage | Preview | Apply | Reverse.

.DESCRIPTION
    Wrapper over Move-Document.sql / Export-PreImage.sql / Reverse-Move.sql (README.md: window steps W-0..W-10).
    One document per transaction, in the order given by -Documents; the first failure stops the run (documents already
    moved stay moved - Reverse them one by one if needed).

      PreImage  creates the side database EcrMovePre_<CorrelationId> (read-only on the ECR database).
      Preview   runs the REAL move + Q-17 relocation in one transaction and ALWAYS rolls back (enforced in the SQL):
                the report is the post-move state with the target calendar. Takes locks and consumes sequence values:
                run it with the worker stopped.
      Apply     same, commits. Needs -CorrelationId (the pre-image of it must exist), -Reason, -ConfirmBackup;
                on EcrLandUi additionally -AllowLive (separate human word).
      Reverse   puts a document back from the pre-image (only while the source project exists). Preview first
                (-ReversePreview), then Apply.

    Refused databases: ECR, FLERT, EcrLegacyProd (legacy / foreign). The SQL refuses as well.

.EXAMPLE
    # rehearsal on a restored copy
    .\Move-Document.ps1 -Database EcrLandB14Reh -Mode PreImage -Documents 98,93 -CorrelationId b14move-reh1
    .\Move-Document.ps1 -Database EcrLandB14Reh -Mode Preview  -Documents 98,93 -TargetProject LANDUI2026 -CorrelationId b14move-reh1
    .\Move-Document.ps1 -Database EcrLandB14Reh -Mode Apply    -Documents 98,93 -TargetProject LANDUI2026 -CorrelationId b14move-reh1 `
        -Reason 'b14: one project LANDUI2026 (human 25.11)' -ConfirmBackup E:\Backup\EcrLandB14Reh.bak
#>
[CmdletBinding()]
param(
    [string]$Server = 'localhost',
    [Parameter(Mandatory = $true)][string]$Database,
    [Parameter(Mandatory = $true)][ValidateSet('PreImage', 'Preview', 'Apply', 'Reverse')][string]$Mode,
    [Parameter(Mandatory = $true)][long[]]$Documents,
    [string]$TargetProject = 'LANDUI2026',
    [Parameter(Mandatory = $true)][string]$CorrelationId,
    [string]$Reason,
    [int]$UserId = 0,
    [switch]$AllowLive,
    [switch]$ReversePreview,
    [string]$ConfirmBackup,
    [int]$BackupMaxAgeHours = 12,
    [string]$PreImageDataPath,
    [string]$OutDir = '.\document-move-out'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'DocumentMove.Lib.ps1')
try { Add-Type -AssemblyName System.Data } catch { }

Assert-MoveDatabase $Database
Assert-CorrelationId $CorrelationId
$preImage = Get-PreImageDatabaseName $CorrelationId
$connectionString = "Server=$Server;Database=$Database;Integrated Security=true;TrustServerCertificate=true;Application Name=ECR-DocumentMove"

function Invoke-MoveSql([string]$text) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    try {
        $cn.Open()
        $cmd = $cn.CreateCommand()
        $cmd.CommandText = $text
        $cmd.CommandTimeout = 0
        $ds = New-Object System.Data.DataSet
        [void](New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($ds)
        return , $ds
    }
    finally { $cn.Dispose() }
}

function Invoke-MoveScalar([string]$text) {
    $cn = New-Object System.Data.SqlClient.SqlConnection $connectionString
    try { $cn.Open(); $cmd = $cn.CreateCommand(); $cmd.CommandText = $text; $cmd.CommandTimeout = 120; return $cmd.ExecuteScalar() }
    finally { $cn.Dispose() }
}

function Save-DataSet($ds, [string]$tag) {
    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    for ($i = 0; $i -lt $ds.Tables.Count; $i++) {
        $table = $ds.Tables[$i]
        if ($table.Rows.Count -eq 0) { continue }
        $name = $table.Columns[0].ColumnName
        $rows = @($table.Rows | ForEach-Object { $r = $_; $o = [ordered]@{}; foreach ($c in $table.Columns) { $o[$c.ColumnName] = $r[$c.ColumnName] }; [pscustomobject]$o })
        $rows | Export-Csv -LiteralPath (Join-Path $OutDir ("{0}-{1}-{2:00}-{3}.csv" -f $CorrelationId, $tag, $i, $name)) -NoTypeInformation -Encoding UTF8
        if ($name -in @('MoveReport', 'ReverseReport', 'PreImage', 'MoveBlocker', 'RightsDelta', 'MoveCopyDiffers')) {
            Write-Host "--- $tag : $name ---"
            $rows | Format-List | Out-String | Write-Host
        }
    }
}

if ((Invoke-MoveScalar "SELECT CASE WHEN OBJECT_ID(N'doc.TableInstance', N'U') IS NULL THEN 0 ELSE 1 END") -ne 1) {
    throw "Move: database '$Database' is not an ECR database."
}
if ($UserId -le 0) {
    $UserId = [int](Invoke-MoveScalar "SELECT MIN(u.Id) FROM sec.[User] u")
}

$writes = $Mode -eq 'Apply' -or ($Mode -eq 'Reverse' -and -not $ReversePreview)
if ($writes) {
    if ($Database -ieq 'EcrLandUi' -and -not $AllowLive) { throw 'Move: EcrLandUi is live; writing needs -AllowLive (separate human word).' }
    if ([string]::IsNullOrWhiteSpace($ConfirmBackup)) { throw 'Move: -ConfirmBackup <.bak taken in this window> is required for writes.' }
    $bak = $ConfirmBackup.Replace("'", "''")
    [void](Invoke-MoveSql "RESTORE VERIFYONLY FROM DISK = N'$bak'")
    $hdr = (Invoke-MoveSql "RESTORE HEADERONLY FROM DISK = N'$bak'").Tables[0].Rows[0]
    if ([string]$hdr['DatabaseName'] -ine $Database) { throw "Move: the backup belongs to '$($hdr['DatabaseName'])', not '$Database'." }
    if (((Get-Date) - [datetime]$hdr['BackupFinishDate']).TotalHours -gt $BackupMaxAgeHours) { throw 'Move: the backup is older than -BackupMaxAgeHours.' }
}
if ($Mode -eq 'Apply') {
    if ([string]::IsNullOrWhiteSpace($Reason) -or $Reason.Trim().Length -lt 10) { throw 'Move: Apply needs -Reason (at least 10 characters).' }
    if ($null -eq (Invoke-MoveScalar "SELECT DB_ID(N'$preImage')") -or (Invoke-MoveScalar "SELECT DB_ID(N'$preImage')") -is [DBNull]) {
        throw "Move: pre-image $preImage not found - run -Mode PreImage first (window step W-2)."
    }
}

Write-Host "document-move $Mode  database=$Database  documents=$($Documents -join ',')  target=$TargetProject  correlation=$CorrelationId"

if ($Mode -eq 'PreImage') {
    $sql = New-PreImageScript ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Export-PreImage.sql'))) $CorrelationId $Documents $PreImageDataPath
    Save-DataSet (Invoke-MoveSql $sql) 'preimage'
    return
}

$moveSql = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Move-Document.sql'))
$q17Sql = [IO.File]::ReadAllText((Join-Path (Split-Path $PSScriptRoot -Parent) 'q17\Preview-MonthColumnConsolidation.sql'))
$reverseSql = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Reverse-Move.sql'))

foreach ($document in $Documents) {
    if ($Mode -eq 'Reverse') {
        $sqlMode = if ($ReversePreview) { 'Preview' } else { 'Apply' }
        $sql = New-ReverseScript -ReverseSql $reverseSql -Mode $sqlMode -DocumentId $document -CorrelationId $CorrelationId -UserId $UserId -AllowLive:$AllowLive
        Save-DataSet (Invoke-MoveSql $sql) "reverse-$sqlMode-$document"
    }
    else {
        $sql = New-MoveScript -MoveSql $moveSql -Q17Sql $q17Sql -Mode $Mode -DocumentId $document -TargetProjectCode $TargetProject `
            -CorrelationId $CorrelationId -Reason $Reason -UserId $UserId -AllowLive:$AllowLive
        Save-DataSet (Invoke-MoveSql $sql) "move-$Mode-$document"
    }
}

Write-Host "Reports: $((Resolve-Path -LiteralPath $OutDir).Path)"
