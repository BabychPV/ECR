# Мінімальний «репетиційний» шаблон LAND_REH: лише таблиці, колонки-аргументи й колонки виходів, потрібні 12 методологіям
# пакета af-land-package.json. Без довідників/правил: це не побудова Land (Build-LandTemplate.ps1 дозволяє лише бази EcrLand*).
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
$spec=Get-Content (Join-Path $PSScriptRoot "land-spec.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$units=@{}; foreach($u in @((Ok GET '/api/v1/units').J)){ $units[[string]$u.code]=[int]$u.id }
$unitOne=$units['one']
$tpl=@((Ok GET '/api/v1/templates?limit=50&q=LAND_REH').J.items) | ?{$_.code -eq 'LAND_REH'} | select -First 1
if(-not $tpl){ $r=Ok POST '/api/v1/templates' @{code='LAND_REH';nameL10n=@{en='Land rehearsal'}}; $tid=if($r.J.templateId){$r.J.templateId}else{$r.J.id}} else {$tid=$tpl.id}
"template $tid"
$r=Ok POST "/api/v1/templates/$tid/versions" @{versionNumber='1.0.0.0';cloneFromVersionId=$null}
$vid=if($r.J.versionId){$r.J.versionId}else{$r.J.id}; "version $vid"
$null=Ok PUT "/api/v1/template-versions/$vid/sheets/S3_5" @{nameL10n=@{en='Land rehearsal'};ordinal=1;sheetGroup=$null;isMandatory=$true;isVisible=$true}
$colIds=@{}
$tord=0
foreach($t in $spec.tables){
  $tord++
  $r=Ok PUT "/api/v1/template-versions/$vid/sheets/S3_5/tables/$($t.code)" @{nameL10n=@{en=$t.code};ordinal=$tord;layoutKind='PerPeriodInstance';rowMode='Dynamic';maxDynamicRows=1000}
  $ttid=$r.J.id
  $ord=0
  foreach($c in $t.columns){
    $ord++
    $dt=$c.type
    $calc=($dt -eq 'Calculated')
    $body=@{headerL10n=@{en=$c.code};ordinal=$ord;dataType=$dt;isRequired=$false;isReadOnly=$calc;isHidden=$calc;precision=$null;scale=$null;defaultValue=$null;displayFormat=$null;styleId=$null;lookupRegistryDefId=$null;lookupFilter=$null;unitId=$null;widthPx=$null;isMonthColumn=$null;monthNumber=$null}
    if($dt -eq 'Decimal'){ $body.precision=34; $body.scale=16 }
    if($dt -eq 'String'){ $body.precision=200 }
    $rc=Ok PUT "/api/v1/template-versions/$vid/tables/$ttid/columns/$($c.code)" $body
    $colIds["$($t.code)/$($c.code)"]=$rc.J.id
  }
  "table $($t.code): columns $ord"
}
$null=Ok POST "/api/v1/template-versions/$vid/publish" @{reason='Land rehearsal'}
"published template version $vid"
$colIds | ConvertTo-Json | Set-Content "$sp\land-colids.json" -Encoding utf8
"templateVersionId=$vid" | Set-Content "$sp\land-state.txt"
