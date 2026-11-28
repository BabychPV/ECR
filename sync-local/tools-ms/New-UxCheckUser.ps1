# Creates a local test user `ux-check` (role LandOwner, copied from land-owner) in the LOCAL stand database
# EcrLandMsLegacy3 for the coordinator's UI checks. Password is generated and written to
# .sync-local\STAND-v5-uxcheck.local.txt (not printed). Never run against live.
param([string]$Database = 'EcrLandMsLegacy3', [string]$Server = 'localhost')
$ErrorActionPreference = 'Stop'
if ($Database -notmatch '^EcrLand|^EcrUpg') { throw "Only local stand databases (EcrLand*/EcrUpg*)." }

$chars = [char[]]'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$bytes = New-Object byte[] 14; $rng.GetBytes($bytes)
$pw = 'Ux-' + (-join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })) + '!7'
$salt = New-Object byte[] 16; $rng.GetBytes($salt)
$kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes($pw, $salt, 200000, [System.Security.Cryptography.HashAlgorithmName]::SHA512)
$hash = '1.200000.' + [Convert]::ToBase64String($salt) + '.' + [Convert]::ToBase64String($kdf.GetBytes(64))

$sql = @"
SET NOCOUNT ON;
DECLARE @id int = (SELECT Id FROM sec.[User] WHERE UserName = 'ux-check');
IF @id IS NULL
BEGIN
  INSERT sec.[User](UserName, DisplayName, Provider, PasswordHash, SecurityStamp, FailedAttempts, MustChangePassword, IsBootstrapAdmin, IsActive, CreatedAt, ReceivesAlerts)
  VALUES ('ux-check', 'UX check (coordinator)', 1, '$hash', REPLACE(CONVERT(nvarchar(36), NEWID()), '-', ''), 0, 0, 0, 1, SYSUTCDATETIME(), 0);
  SET @id = SCOPE_IDENTITY();
  INSERT sec.RoleAssignment(RoleId, UserId) SELECT RoleId, @id FROM sec.RoleAssignment WHERE UserId = (SELECT Id FROM sec.[User] WHERE UserName = 'land-owner');
END
ELSE
  UPDATE sec.[User] SET PasswordHash = '$hash', FailedAttempts = 0, LockedUntil = NULL, IsActive = 1 WHERE Id = @id;
SELECT u.UserName, r.Code FROM sec.[User] u JOIN sec.RoleAssignment ra ON ra.UserId = u.Id JOIN sec.Role r ON r.Id = ra.RoleId WHERE u.UserName = 'ux-check';
"@
sqlcmd -S $Server -E -d $Database -I -b -h -1 -W -Q $sql
if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed" }

$out = Join-Path $PSScriptRoot '..\STAND-v5-uxcheck.local.txt'
"# Local stand $Database only. Test account for coordinator UI checks.`nuser=ux-check`npassword=$pw" | Set-Content -Path $out -Encoding utf8
Write-Host "ux-check ready; password saved to $out"
