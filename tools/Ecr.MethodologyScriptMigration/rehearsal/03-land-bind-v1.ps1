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
$units=@{}; foreach($u in @((Ok GET '/api/v1/units').J)){ $units[[string]$u.code]=[int]$u.id }
"units: " + (($units.Keys|sort) -join ',')
$u1=$units['one']
$col=Get-Content "$sp\land-colids.json" -Raw | ConvertFrom-Json
$AF=@{ECW_C05_01='T3_00';ECW_C05_02='T3_00';ECW_C05_03='T3_00';ECW_C05_04='T3_00';ECW_C05_05='T3_00';ECW_C06_01='T4_01';ECW_C07_07='T5_08';ECW_C07_08='T5_09';ECW_C07_21='T5_27'}
$ms=@((Ok GET "/api/v1/methodologies").J); "ms count: $($ms.Count) first: $($ms[0].code)"
foreach($mm in $ms){ $m=$mm
  $code=[string]$m.code; if(-not $AF.ContainsKey($code)){continue}
  $mid=[int]$m.id
  $vers=@((Ok GET "/api/v1/methodologies/$mid/versions").J)
  $vid=[int]($vers|sort id -Descending|select -First 1).id
  $fx=@((Ok GET "/api/v1/methodologies/$mid/versions/$vid/formulas").J)
  $outs=@($fx|%{[string]$_.code}|?{$_ -match '^ECW_EC_'}|sort -Unique)
  $o=0;$b=0
  foreach($c in $outs){
    $o++
    $uid=$u1
    if($c -match '_tons_' -and $units.ContainsKey('t')){$uid=$units['t']} elseif($c -match '_gsec_' -and $units.ContainsKey('g_per_s')){$uid=$units['g_per_s']}
    $null=Ok PUT "/api/v1/methodologies/$mid/versions/$vid/outputs/$c" @{ordinal=$o;unitId=$uid}
    $cid=$col."$($AF[$code])/$c"
    if($cid){ $null=Ok PUT "/api/v1/methodologies/$mid/bindings/$cid/$c" @{matchJson='{}';isActive=$true}; $b++ }
  }
  "$code outputs=$o bindings=$b"
}
