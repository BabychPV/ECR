# b14 document-move (D-579): composition of the move / pre-image / reverse SQL. ONE place that builds the text
# executed by Move-Document.ps1; tests/Ecr.Infrastructure.Tests/Persistence/DocumentMoveSqlTests.cs mirrors it
# (same markers, same substitution) and DocumentMove-Lib Pester tests pin it.

Set-StrictMode -Version Latest

$script:MoveApplyToken = 'MOVE-APPLY-v1'
$script:Q17ApplyToken = 'Q17-APPLY-v1'
$script:RefusedDatabases = @('ECR', 'FLERT', 'EcrLegacyProd')

function Assert-MoveDatabase([string]$Database) {
    if ([string]::IsNullOrWhiteSpace($Database)) { throw 'Move: -Database is required (no default).' }
    foreach ($refused in $script:RefusedDatabases) {
        if ($Database -ieq $refused) { throw "Move: refusing database '$Database' (legacy / foreign)." }
    }
}

function Assert-CorrelationId([string]$CorrelationId) {
    if ($CorrelationId -notmatch '^[A-Za-z0-9_-]{6,48}$') {
        throw "Move: -CorrelationId must be 6..48 characters [A-Za-z0-9_-] (it names the pre-image database)."
    }
}

function Get-PreImageDatabaseName([string]$CorrelationId) {
    Assert-CorrelationId $CorrelationId
    return 'EcrMovePre_' + $CorrelationId
}

function ConvertTo-SqlLiteral($Value) {
    if ($null -eq $Value) { return 'NULL' }
    if ($Value -is [bool]) { return $(if ($Value) { '1' } else { '0' }) }
    if ($Value -is [int] -or $Value -is [long]) { return [string]$Value }
    return "N'" + ([string]$Value).Replace("'", "''") + "'"
}

# Replaces the text between "-- BEGIN PARAMS" and "-- END PARAMS" (markers included) with $Header.
function Set-ParamBlock([string]$Sql, [string]$Header) {
    $begin = $Sql.IndexOf('-- BEGIN PARAMS')
    $end = $Sql.IndexOf('-- END PARAMS')
    if ($begin -lt 0 -or $end -lt $begin) { throw 'Move: SQL has no BEGIN/END PARAMS block.' }
    $end += '-- END PARAMS'.Length
    return $Sql.Substring(0, $begin) + $Header + $Sql.Substring($end)
}

# The Q-17 consolidation text for ONE document, Apply mode, closed owners written (system relocation), live guard satisfied:
# the move wrapper owns the guard (token + -AllowLive + reason) and the transaction.
function New-Q17ApplyText([string]$Q17Sql, [long]$DocumentId, [int]$UserId, [string]$CorrelationId) {
    $header = @"
DECLARE @Mode        varchar(10)  = 'Apply';
DECLARE @DocumentId  bigint       = $DocumentId;
DECLARE @Year        int          = NULL;
DECLARE @ColumnScope varchar(10)  = 'All';
DECLARE @RunId       nvarchar(64) = $(ConvertTo-SqlLiteral $CorrelationId);
DECLARE @UserId      int          = $UserId;
DECLARE @WriteLocked bit          = 1;
DECLARE @AllowLive   bit          = 1;
DECLARE @ApplyToken  varchar(40)  = '$script:Q17ApplyToken';
"@
    return Set-ParamBlock $Q17Sql $header
}

function New-MoveScript {
    param(
        [Parameter(Mandatory = $true)][string]$MoveSql,
        [Parameter(Mandatory = $true)][string]$Q17Sql,
        [Parameter(Mandatory = $true)][ValidateSet('Preview', 'Apply')][string]$Mode,
        [Parameter(Mandatory = $true)][long]$DocumentId,
        [Parameter(Mandatory = $true)][string]$TargetProjectCode,
        [Parameter(Mandatory = $true)][string]$CorrelationId,
        [string]$Reason,
        [Parameter(Mandatory = $true)][int]$UserId,
        [switch]$AllowLive,
        [string]$PreImage,
        [int]$LockTimeoutMs = 60000
    )

    $header = @"
DECLARE @MvMode             varchar(10)    = '$Mode';
DECLARE @MvDocumentId        bigint         = $DocumentId;
DECLARE @MvTargetProjectCode nvarchar(64)   = $(ConvertTo-SqlLiteral $TargetProjectCode);
DECLARE @MvCorrelationId     nvarchar(64)   = $(ConvertTo-SqlLiteral $CorrelationId);
DECLARE @MvReason            nvarchar(1000) = $(ConvertTo-SqlLiteral $(if ([string]::IsNullOrWhiteSpace($Reason)) { $null } else { $Reason }));
DECLARE @MvUserId            int            = $UserId;
DECLARE @MvAllowLive         bit            = $(if ($AllowLive) { 1 } else { 0 });
DECLARE @MvApplyToken        varchar(40)    = '$script:MoveApplyToken';
DECLARE @MvPreImage          sysname        = $(ConvertTo-SqlLiteral $(if ([string]::IsNullOrWhiteSpace($PreImage)) { $null } else { $PreImage }));
DECLARE @MvLockTimeout       int            = $LockTimeoutMs;
"@
    $text = Set-ParamBlock $MoveSql $header
    $marker = '-- @@Q17@@'
    if ($text.IndexOf($marker) -lt 0) { throw 'Move: SQL has no Q-17 placeholder.' }
    return $text.Replace($marker, (New-Q17ApplyText $Q17Sql $DocumentId $UserId $CorrelationId))
}

function New-PreImageScript([string]$PreImageSql, [string]$CorrelationId, [long[]]$Documents, [string]$DataPath) {
    $db = Get-PreImageDatabaseName $CorrelationId
    $header = @"
DECLARE @PiDatabase      sysname       = $(ConvertTo-SqlLiteral $db);
DECLARE @PiDocuments     nvarchar(400) = $(ConvertTo-SqlLiteral (($Documents | ForEach-Object { [string]$_ }) -join ','));
DECLARE @PiCorrelationId nvarchar(64)  = $(ConvertTo-SqlLiteral $CorrelationId);
DECLARE @PiDataPath      nvarchar(400) = $(ConvertTo-SqlLiteral $(if ([string]::IsNullOrWhiteSpace($DataPath)) { $null } else { $DataPath }));
"@
    return Set-ParamBlock $PreImageSql $header
}

function New-ReverseScript {
    param(
        [Parameter(Mandatory = $true)][string]$ReverseSql,
        [Parameter(Mandatory = $true)][ValidateSet('Preview', 'Apply')][string]$Mode,
        [Parameter(Mandatory = $true)][long]$DocumentId,
        [Parameter(Mandatory = $true)][string]$CorrelationId,
        [Parameter(Mandatory = $true)][int]$UserId,
        [switch]$AllowLive
    )

    $header = @"
DECLARE @RvMode          varchar(10)  = '$Mode';
DECLARE @RvDocumentId    bigint       = $DocumentId;
DECLARE @RvPreImage      sysname      = $(ConvertTo-SqlLiteral (Get-PreImageDatabaseName $CorrelationId));
DECLARE @RvCorrelationId nvarchar(64) = $(ConvertTo-SqlLiteral $CorrelationId);
DECLARE @RvUserId        int          = $UserId;
DECLARE @RvAllowLive     bit          = $(if ($AllowLive) { 1 } else { 0 });
DECLARE @RvApplyToken    varchar(40)  = '$script:MoveApplyToken';
"@
    return Set-ParamBlock $ReverseSql $header
}
