# -MissingEvery N: у середньому кожна N-та комірка вводу лишається порожньою (за замовчуванням 15, як у репетиції); 0 — повний ввід.
param([int]$MissingEvery=15)
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
$model=Get-Content (Join-Path $PSScriptRoot '..\..\hse400\hse400-model.json') -Raw -Encoding UTF8|ConvertFrom-Json
$docId=3; $period=202601
$list=@((Ok GET "/api/v1/documents/$docId/tables?periodKey=$period").J)
$rng=New-Object System.Random 4242
foreach($tb in $model.tables){
  $ti=[int](($list|?{[string]$_.tableCode -eq $tb.code}|select -First 1).tableInstanceId)
  $rows=@()
  foreach($row in $tb.rows){
    if([string]$row.kind -ne 'Input'){continue}
    $cells=@()
    foreach($mn in 1..12){
      $v=[decimal][Math]::Round(0.1+$rng.NextDouble()*40,4)
      if($MissingEvery -gt 0 -and $rng.Next(0,$MissingEvery) -eq 0){ continue }   # пропущені місяці: порожні комірки
      $cells+=@{columnCode=('M{0:00}' -f $mn);isEmpty=$false;value=$v}
    }
    $rows+=@{rowKey=[string]$row.key;baseVersion=$null;cells=$cells}
  }
  $slice=(Ok GET "/api/v1/documents/$docId/tables/$ti`?periodKey=$period").J
  $rv=@{}; foreach($r in @($slice.rows)){ if($r){$rv[[string]$r.rowKey]=[string]$r.rowVersion} }
  foreach($r in $rows){ if($rv.ContainsKey($r.rowKey)){$r.baseVersion=$rv[$r.rowKey]} }
  $res=Call PATCH "/api/v1/documents/$docId/cells" @{origin='UserEdit';periodKey=$period;tableInstanceId=$ti;rows=$rows}
  "$($tb.code): rows=$($rows.Count) PATCH -> $($res.S) $($res.T)"
}
