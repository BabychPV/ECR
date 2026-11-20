$ErrorActionPreference='Stop'
$sp=$env:MS_REHEARSAL_DIR; if(-not $sp){ throw 'Задайте MS_REHEARSAL_DIR (тека для паролів і проміжних файлів, поза репозиторієм).' }
$base='http://localhost:5092'
$sess=New-Object Microsoft.PowerShell.Commands.WebRequestSession
function Call($m,$p,$b){
  $a=@{Uri="$base$p";Method=$m;WebSession=$sess;UseBasicParsing=$true;ContentType='application/json; charset=utf-8'}
  if($null -ne $b){$a.Body=[Text.Encoding]::UTF8.GetBytes(($b|ConvertTo-Json -Depth 12 -Compress))}
  try{ $r=Invoke-WebRequest @a; $j=$null; if($r.Content){$j=$r.Content|ConvertFrom-Json}; return [pscustomobject]@{S=[int]$r.StatusCode;J=$j} }
  catch { $resp=$_.Exception.Response; $t=''; if($resp){$sr=New-Object IO.StreamReader($resp.GetResponseStream()); $t=$sr.ReadToEnd()}; return [pscustomobject]@{S=[int]$resp.StatusCode;J=$null;T=$t} }
}
function Ok($m,$p,$b){ $r=Call $m $p $b; if($r.S -ge 300){ throw "$m $p -> $($r.S) $($r.T)" }; return $r }
$op=(Get-Content "$sp\owner.pw" -Raw).Trim()
$null=Ok POST '/api/v1/login/local' @{userName='ms-owner';password=$op}
$docId=[int]((Get-Content "$sp\land-doc.json" -Raw|ConvertFrom-Json).documentId)
$tests=Get-Content (Join-Path $PSScriptRoot "land-tests.json") -Raw -Encoding UTF8|ConvertFrom-Json
foreach($month in 1..12){
$period=202600+$month
$mf=1+($month*0.07)
$inst=(Ok GET "/api/v1/documents/$docId/tables?periodKey=$period").J
$list=@($inst)
"instances: " + (($list|%{"$($_.tableCode)=$($_.tableInstanceId)"}) -join ', ')
$AF=@{ECW_C05_01='T3_00';ECW_C05_02='T3_00';ECW_C05_03='T3_00';ECW_C05_04='T3_00';ECW_C05_05='T3_00';ECW_C06_01='T4_01';ECW_C07_07='T5_08';ECW_C07_08='T5_09';ECW_C07_21='T5_27'}
$rng=New-Object System.Random 20261118
function Cells($inp,$scale){
  $cells=@()
  foreach($pp in $inp.PSObject.Properties){
    if($pp.Value -is [string]){ $cells+=@{columnCode=$pp.Name;isEmpty=$false;value=$pp.Value} }
    else { $v=[decimal]([double]$pp.Value*$scale); $cells+=@{columnCode=$pp.Name;isEmpty=$false;value=$v} }
  }
  return ,$cells
}
$byTable=@{}
$scales=@((1.0*$mf),(0.5*$mf),(2.0*$mf))
$n=0
foreach($code in $AF.Keys){
  $tc=$AF[$code]; if(-not $byTable.ContainsKey($tc)){$byTable[$tc]=@()}
  $te=$tests.$code
  foreach($sc in $scales){
    $n++
    $byTable[$tc]+=,@{rowKey=("R{0:000}" -f $n);cells=(Cells $te.inp $sc)}
  }
}
# рядок із неповним вводом (лише текстові ключі) - перевірка MissingArguments в обох рушіях
foreach($tc in @($byTable.Keys)){ $n++; $byTable[$tc]+=,@{rowKey=("R{0:000}" -f $n);cells=@(@{columnCode=$(if($tc -eq 'T5_27'){'Land_TypeBurnFuel'}elseif($tc -eq 'T5_09'){'Land_TypePetrStation'}elseif($tc -eq 'T5_08'){'Land_TypeStorMaterial'}else{'Land_TypeFuel'});isEmpty=$false;value='Diesel'})} }
foreach($tc in $byTable.Keys){
  $ti=[int](($list|?{[string]$_.tableCode -eq $tc}|select -First 1).tableInstanceId)
  $body=@{origin='UserEdit';periodKey=$period;tableInstanceId=$ti;rows=@($byTable[$tc]|%{@{rowKey=$_.rowKey;baseVersion=$null;cells=@($_.cells)}})}
  $r=Call PATCH "/api/v1/documents/$docId/cells" $body
  "$tc rows=$(@($byTable[$tc]).Count) PATCH -> $($r.S) $($r.T)"
}

}
