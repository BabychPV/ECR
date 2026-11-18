$ErrorActionPreference='Stop'
$sp=$env:MS_REHEARSAL_DIR; if(-not $sp){ throw 'Задайте MS_REHEARSAL_DIR (тека для паролів і проміжних файлів, поза репозиторієм).' }
$base='http://localhost:5092'
$sess = New-Object Microsoft.PowerShell.Commands.WebRequestSession
function Call($m,$p,$b){ 
  $args=@{Uri="$base$p";Method=$m;WebSession=$sess;UseBasicParsing=$true;ContentType='application/json; charset=utf-8'}
  if($b){$args.Body=[Text.Encoding]::UTF8.GetBytes($b)}
  try{ $r=Invoke-WebRequest @args; return $r } catch { $resp=$_.Exception.Response; $t=''; if($resp){$sr=New-Object IO.StreamReader($resp.GetResponseStream()); $t=$sr.ReadToEnd()}; return [pscustomobject]@{StatusCode=[int]$resp.StatusCode;Content=$t} }
}
$pwFile="$sp\bootstrap.pw"
$pw='Dev-Bootstrap-2026!'
if(Test-Path $pwFile){$pw=(Get-Content $pwFile -Raw).Trim()}
$r=Call POST '/api/v1/login/local' (@{userName='bootstrap';password=$pw}|ConvertTo-Json)
"login: $($r.StatusCode) $($r.Content.Substring(0,[Math]::Min(300,$r.Content.Length)))"
if(-not (Test-Path $pwFile)){
  $new='Ms'+[guid]::NewGuid().ToString('N').Substring(0,14)+'!Aa1'
  $c=Call POST '/api/v1/auth/change-password' (@{currentPassword=$pw;newPassword=$new}|ConvertTo-Json)
  "change-password: $($c.StatusCode) $($c.Content)"
  if($c.StatusCode -in 200,204){ $new | Set-Content $pwFile -Encoding ascii; $r=Call POST '/api/v1/login/local' (@{userName='bootstrap';password=$new}|ConvertTo-Json); "relogin: $($r.StatusCode)" }
}

$ownPw="$sp\owner.pw"
if(-not (Test-Path $ownPw)){
  $perms=@(((Call GET '/api/v1/permissions').Content|ConvertFrom-Json)|%{[string]$_.code})
  $rr=Call POST '/api/v1/roles' (@{code='MsCorpusOwner';nameL10n=@{en='MsCorpusOwner'};permissionCodes=$perms}|ConvertTo-Json -Depth 5)
  "role: $($rr.StatusCode) $($rr.Content.Substring(0,[Math]::Min(200,$rr.Content.Length)))"
  $init='Ms'+[guid]::NewGuid().ToString('N').Substring(0,14)+'!Aa1'
  $uu=Call POST '/api/v1/users' (@{userName='ms-owner';provider='Local';sid=$null;displayName='ms-owner';initialPassword=$init;roleCodes=@('MsCorpusOwner')}|ConvertTo-Json -Depth 5)
  "user: $($uu.StatusCode) $($uu.Content.Substring(0,[Math]::Min(200,$uu.Content.Length)))"
  $s2 = New-Object Microsoft.PowerShell.Commands.WebRequestSession
  $x=Invoke-WebRequest -Uri "$base/api/v1/login/local" -Method POST -WebSession $s2 -UseBasicParsing -ContentType 'application/json' -Body (@{userName='ms-owner';password=$init}|ConvertTo-Json)
  $fin='Ms'+[guid]::NewGuid().ToString('N').Substring(0,14)+'!Aa1'
  $y=Invoke-WebRequest -Uri "$base/api/v1/auth/change-password" -Method POST -WebSession $s2 -UseBasicParsing -ContentType 'application/json' -Body (@{currentPassword=$init;newPassword=$fin}|ConvertTo-Json)
  "owner pw change: $($y.StatusCode)"
  $fin | Set-Content $ownPw -Encoding ascii
}
$op=(Get-Content $ownPw -Raw).Trim()
$sess = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$r=Call POST '/api/v1/login/local' (@{userName='ms-owner';password=$op}|ConvertTo-Json)
"owner login: $($r.StatusCode)"
$pkgPath=(Join-Path $PSScriptRoot '..\..\..\docs\delivery\reference-data\land-template\af-land-package.json')
$pkg=[IO.File]::ReadAllText($pkgPath,[Text.Encoding]::UTF8)
$d=Call POST '/api/v1/methodologies/import?dryRun=true&timeZone=Asia%2FAtyrau' $pkg
"dry: $($d.StatusCode) $($d.Content.Substring(0,[Math]::Min(600,$d.Content.Length)))"
if($d.StatusCode -eq 200){
 $i=Call POST '/api/v1/methodologies/import?dryRun=false&timeZone=Asia%2FAtyrau' $pkg
 "import: $($i.StatusCode) $($i.Content.Substring(0,[Math]::Min(800,$i.Content.Length)))"
}
