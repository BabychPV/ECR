$ErrorActionPreference='Stop'
$sp=$env:MS_REHEARSAL_DIR; if(-not $sp){ throw 'Задайте MS_REHEARSAL_DIR (тека для паролів і проміжних файлів, поза репозиторієм).' }
$base='http://localhost:5092'
$sess = New-Object Microsoft.PowerShell.Commands.WebRequestSession
function Call($m,$p,$b){
  $a=@{Uri="$base$p";Method=$m;WebSession=$sess;UseBasicParsing=$true;ContentType='application/json; charset=utf-8'}
  if($null -ne $b){$a.Body=[Text.Encoding]::UTF8.GetBytes(($b|ConvertTo-Json -Depth 8 -Compress))}
  try{ $r=Invoke-WebRequest @a; $j=$null; if($r.Content){$j=$r.Content|ConvertFrom-Json}; return [pscustomobject]@{S=[int]$r.StatusCode;J=$j} }
  catch { $resp=$_.Exception.Response; $t=''; if($resp){$sr=New-Object IO.StreamReader($resp.GetResponseStream()); $t=$sr.ReadToEnd()}; return [pscustomobject]@{S=[int]$resp.StatusCode;J=$null;T=$t} }
}
function Ok($m,$p,$b){ $r=Call $m $p $b; if($r.S -ge 300){ throw "$m $p -> $($r.S) $($r.T)" }; return $r }
$op=(Get-Content "$sp\owner.pw" -Raw).Trim()
$null=Ok POST '/api/v1/login/local' @{userName='ms-owner';password=$op}
$col=Get-Content "$sp\land-colids.json" -Raw | ConvertFrom-Json
$AF=@{ECW_C05_01='T3_00';ECW_C05_02='T3_00';ECW_C05_03='T3_00';ECW_C05_04='T3_00';ECW_C05_05='T3_00';ECW_C06_01='T4_01';ECW_C07_07='T5_08';ECW_C07_08='T5_09';ECW_C07_21='T5_27'}
$ms=@((Ok GET "/api/v1/methodologies").J)
foreach($mm in $ms){
  $code=[string]$mm.code; if(-not $AF.ContainsKey($code)){continue}
  $mid=[int]$mm.id
  $vid=[int](@((Ok GET "/api/v1/methodologies/$mid/versions").J)|sort id -Descending|select -First 1).id
  $rules=@()
  if($code -eq 'ECW_C07_21'){
    $c=$col."T5_27/Land_TypeBurnFuel"
    $rules=@(@{n='R_DIESEL';m=(@{"$c"='Diesel'}|ConvertTo-Json -Compress);p=10},@{n='R_LPG';m=(@{"$c"='LPG'}|ConvertTo-Json -Compress);p=20})
  } elseif($code -eq 'ECW_C07_08'){
    $c=$col."T5_09/Land_TypePetrStation"
    $rules=@(@{n='R_GROUND';m=(@{"$c"='Ground level tank'}|ConvertTo-Json -Compress);p=10})
  } else { $rules=@(@{n='R_ALL';m='{}';p=10}) }
  foreach($r in $rules){ $null=Ok PUT "/api/v1/methodologies/$mid/versions/$vid/rules/$($r.n)" @{matchJson=$r.m;priority=$r.p;isActive=$true} }
  "$code rules=$($rules.Count)"
}
