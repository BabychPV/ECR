$ErrorActionPreference='Stop'
$sp=$env:MS_REHEARSAL_DIR; if(-not $sp){ throw 'Задайте MS_REHEARSAL_DIR (тека для паролів і проміжних файлів, поза репозиторієм).' }
$base='http://localhost:5092'
function NewSess($u,$pw){ $s=New-Object Microsoft.PowerShell.Commands.WebRequestSession; $null=Invoke-WebRequest "$base/api/v1/login/local" -Method POST -WebSession $s -UseBasicParsing -ContentType 'application/json' -Body (@{userName=$u;password=$pw}|ConvertTo-Json); return $s }
$sess=$null
function Call($m,$p,$b){
  $a=@{Uri="$base$p";Method=$m;WebSession=$sess;UseBasicParsing=$true;ContentType='application/json; charset=utf-8'}
  if($null -ne $b){$a.Body=[Text.Encoding]::UTF8.GetBytes(($b|ConvertTo-Json -Depth 10 -Compress))}
  try{ $r=Invoke-WebRequest @a; $j=$null; if($r.Content){$j=$r.Content|ConvertFrom-Json}; return [pscustomobject]@{S=[int]$r.StatusCode;J=$j} }
  catch { $resp=$_.Exception.Response; $t=''; if($resp){$sr=New-Object IO.StreamReader($resp.GetResponseStream()); $t=$sr.ReadToEnd()}; return [pscustomobject]@{S=[int]$resp.StatusCode;J=$null;T=$t} }
}
function Ok($m,$p,$b){ $r=Call $m $p $b; if($r.S -ge 300){ throw "$m $p -> $($r.S) $($r.T)" }; return $r }
$op=(Get-Content "$sp\owner.pw" -Raw).Trim()
$sess=NewSess 'ms-owner' $op
# --- reviewer ---
$revPw="$sp\reviewer.pw"
if(-not (Test-Path $revPw)){
  $init='Ms'+[guid]::NewGuid().ToString('N').Substring(0,14)+'!Aa1'
  $null=Ok POST '/api/v1/users' @{userName='ms-reviewer';provider='Local';sid=$null;displayName='ms-reviewer';initialPassword=$init;roleCodes=@('MsCorpusOwner')}
  $s2=NewSess 'ms-reviewer' $init; $fin='Ms'+[guid]::NewGuid().ToString('N').Substring(0,14)+'!Aa1'
  $null=Invoke-WebRequest "$base/api/v1/auth/change-password" -Method POST -WebSession $s2 -UseBasicParsing -ContentType 'application/json' -Body (@{currentPassword=$init;newPassword=$fin}|ConvertTo-Json)
  $fin|Set-Content $revPw -Encoding ascii
}
# --- template v2 ---
$extra=@{T3_00=@(@{c='Land_TypeFuel';t='String'});T4_01=@(@{c='Land_TypeFuel';t='String'});T5_08=@(@{c='Land_TypeStorMaterial';t='String'});T5_09=@(@{c='Land_TypePumpedLiquid';t='String'});T5_27=@(@{c='CalculationMonth';t='Decimal'})}
$st2="$sp\land-v2.json"
if(-not (Test-Path $st2)){
  $tvs=@((Ok GET '/api/v1/templates/3/versions').J.items)
  $dv=$tvs|?{([string]$_.version -eq '1.0.1.0') -or ([string]$_.versionNumber -eq '1.0.1.0')}|select -First 1
  if($dv){$v2n=[int]$dv.id}else{ $r=Ok POST '/api/v1/templates/3/versions' @{versionNumber='1.0.1.0';cloneFromVersionId=3}; $v2n=if($r.J.versionId){[int]$r.J.versionId}else{[int]$r.J.id} }
  $struct=(Ok GET "/api/v1/template-versions/$v2n/structure").J
  $tabs=@{}; $idx=@{}; foreach($sh in $struct.sheets){ foreach($t in $sh.tables){ $tabs[[string]$t.code]=[int]$t.id; foreach($c in $t.columns){ $idx["$($t.code)/$($c.code)"]=[int]$c.id } } }
  foreach($tc in $extra.Keys){ foreach($x in $extra[$tc]){
    $isDec=($x.t -eq 'Decimal')
    $body=@{headerL10n=@{en=$x.c};ordinal=100;dataType=$x.t;isRequired=$false;isReadOnly=$false;isHidden=$false;precision=$(if($isDec){34}else{200});scale=$(if($isDec){16}else{$null});defaultValue=$null;displayFormat=$null;styleId=$null;lookupRegistryDefId=$null;lookupFilter=$null;unitId=$null;widthPx=$null;isMonthColumn=$null;monthNumber=$null}
    $null=Ok PUT "/api/v1/template-versions/$v2n/tables/$($tabs[$tc])/columns/$($x.c)" $body } }
  # клон успадкував активні прив'язки до неопублікованих методологій: вимикаємо (вони стануть активними після публікації методологій)
  $AF0=@{ECW_C05_01='T3_00';ECW_C05_02='T3_00';ECW_C05_03='T3_00';ECW_C05_04='T3_00';ECW_C05_05='T3_00';ECW_C06_01='T4_01';ECW_C07_07='T5_08';ECW_C07_08='T5_09';ECW_C07_21='T5_27'}
  foreach($mm in @((Ok GET '/api/v1/methodologies').J)){
    $code=[string]$mm.code; if(-not $AF0.ContainsKey($code)){continue}
    $mid=[int]$mm.id
    $vid=[int]((@((Ok GET "/api/v1/methodologies/$mid/versions").J)|sort id -Descending|select -First 1).id)
    $outs=@(@((Ok GET "/api/v1/methodologies/$mid/versions/$vid/formulas").J)|%{[string]$_.code}|?{$_ -match '^ECW_EC_'}|sort -Unique)
    foreach($o in $outs){ $cid=$idx["$($AF0[$code])/$o"]; if($cid){ $null=Ok PUT "/api/v1/methodologies/$mid/bindings/$cid/$o" @{matchJson='{}';isActive=$false} } }
  }
  $null=Ok POST "/api/v1/template-versions/$v2n/publish" @{reason='Land rehearsal v2'}
  $struct=(Ok GET "/api/v1/template-versions/$v2n/structure").J
  $ids=@{}; $sheetIds=@(); foreach($sh in $struct.sheets){ $sheetIds+=[int]$sh.id; foreach($t in $sh.tables){ foreach($c in $t.columns){ $ids["$($t.code)/$($c.code)"]=[int]$c.id } } }
  @{v2=$v2n;ids=$ids;sheetIds=$sheetIds}|ConvertTo-Json -Depth 5|Set-Content $st2 -Encoding utf8
  "template v2 published: $v2n columns $($ids.Count)"
}
$S=Get-Content $st2 -Raw|ConvertFrom-Json
$v2=[int]$S.v2
$id2=@{}; $S.ids.PSObject.Properties|%{$id2[$_.Name]=[int]$_.Value}
# --- project + doc ---
$proj=@((Ok GET '/api/v1/projects?limit=200').J)
if($proj.Count -eq 1 -and $proj[0].items){$proj=@($proj[0].items)}
$pr=$proj|?{$_.code -eq 'LAND_REH_2026'}|select -First 1
if(-not $pr){
  $pol=@((Ok GET '/api/v1/projects/period-policies').J)
  if($pol.Count -eq 1 -and $pol[0].items){$pol=@($pol[0].items)}
  $polId=[int]($pol|?{$_.code -eq 'HSE400_OPEN'}|select -First 1).id
  $r=Ok POST '/api/v1/projects' @{code='LAND_REH_2026';nameL10n=@{en='Land rehearsal 2026';ru='Land rehearsal 2026'};timeZoneId='Asia/Atyrau';periodKind='Monthly';year=2026;templateVersionId=$v2;periodPolicyId=$polId;customPeriodCount=$null}
  $projId=if($r.J.projectId){$r.J.projectId}else{$r.J.id}
  $null=Ok POST "/api/v1/projects/$projId/activate"
  $r=Ok POST '/api/v1/documents' @{name=@{en='Land rehearsal 2026'};projectId=$projId;sheetDefIds=@($S.sheetIds);templateVersionId=$v2}
  $docId=if($r.J.documentId){$r.J.documentId}else{$r.J.id}
  "project $projId document $docId"
  @{projectId=$projId;documentId=$docId}|ConvertTo-Json|Set-Content "$sp\land-doc.json"
}
$docId=[int]((Get-Content "$sp\land-doc.json" -Raw|ConvertFrom-Json).documentId)
# --- bindings on v2 columns, rules, category rules, tests, publish ---
$AF=@{ECW_C05_01='T3_00';ECW_C05_02='T3_00';ECW_C05_03='T3_00';ECW_C05_04='T3_00';ECW_C05_05='T3_00';ECW_C06_01='T4_01';ECW_C07_07='T5_08';ECW_C07_08='T5_09';ECW_C07_21='T5_27'}
$CAT=@{
 ECW_C05_02='!ECW_Category'
 ECW_C05_04="if(@Land_TypeFuel = 'Diesel', 'Diesel', if(@Land_TypeFuel = 'Kerosene', 'Kerosene', ''))"
 ECW_C06_01="if(@Land_TypeFuel = 'Diesel', 'Diesel', if(@Land_TypeFuel = 'Gasoline', 'Gasoline', ''))"
 ECW_C07_07="if(@Land_TypeStorMaterial = 'Diesel', 'Diesel', if(@Land_TypeStorMaterial = 'Gasoline', 'Gasoline', if(@Land_TypeStorMaterial = 'Bilge water' or @Land_TypeStorMaterial = 'Lubricant', 'Base', if(@Land_TypeStorMaterial = 'Recovered oil', 'Recovered', if(@Land_TypeStorMaterial = 'Jet fuel' or @Land_TypeStorMaterial = 'Solvent', 'Kerosene', '')))))"
 ECW_C07_08="if(@Land_TypePumpedLiquid = 'Diesel', 'Diesel', if(@Land_TypePumpedLiquid = 'Gasoline', 'Gasoline', if(@Land_TypePumpedLiquid = 'Jet fuel', 'Kerosene', if(@Land_TypePumpedLiquid = 'Lubricant', 'MineralOil', ''))))"
 ECW_C07_21='!ECW_Category'
}
$tests=Get-Content (Join-Path $PSScriptRoot "land-tests.json") -Raw -Encoding UTF8|ConvertFrom-Json
$ms=@((Ok GET '/api/v1/methodologies').J)
$revPass=(Get-Content $revPw -Raw).Trim()
foreach($mm in $ms){
  $code=[string]$mm.code; if(-not $AF.ContainsKey($code)){continue}
  $mid=[int]$mm.id; $tc=$AF[$code]
  $vs=@((Ok GET "/api/v1/methodologies/$mid/versions").J)|sort id -Descending|select -First 1
  $vid=[int]$vs.id
  if([string]$vs.status -eq 'Published'){ "$code already published"; continue }
  $fx=@((Ok GET "/api/v1/methodologies/$mid/versions/$vid/formulas").J)
  $outs=@($fx|%{[string]$_.code}|?{$_ -match '^ECW_EC_'}|sort -Unique)
  foreach($o in $outs){ $cid=$id2["$tc/$o"]; $null=Ok PUT "/api/v1/methodologies/$mid/bindings/$cid/$o" @{matchJson='{}';isActive=$false} }
  $rules=@()
  if($code -eq 'ECW_C07_21'){ $c=$id2['T5_27/Land_TypeBurnFuel']; $rules=@(@{n='R_DIESEL';m=(@{"$c"='Diesel - Дизель'}|ConvertTo-Json -Compress);p=10},@{n='R_LPG';m=(@{"$c"='LPG'}|ConvertTo-Json -Compress);p=20}) }
  elseif($code -eq 'ECW_C07_08'){ $c=$id2['T5_09/Land_TypePetrStation']; $rules=@(@{n='R_GROUND';m=(@{"$c"='Ground level tank - Наземный резервуар'}|ConvertTo-Json -Compress);p=10}) }
  foreach($r in $rules){ $null=Ok PUT "/api/v1/methodologies/$mid/versions/$vid/rules/$($r.n)" @{matchJson=$r.m;priority=$r.p;isActive=$true} }
  if($CAT.ContainsKey($code)){ $x=Call PUT "/api/v1/methodologies/$mid/versions/$vid/category-rule" @{expression=$CAT[$code]}; "  $code category-rule HTTP $($x.S) $($x.T)" }
  $te=$tests.$code
  $args_=@(); foreach($pp in $te.inp.PSObject.Properties){ if($pp.Value -is [string]){$args_+=@{argumentCode=$pp.Name;value=$null;valueString=$pp.Value;unitId=$null}} else {$args_+=@{argumentCode=$pp.Name;value=[double]$pp.Value;valueString=$null;unitId=$null}} }
  $inputJson=@{documentId=$docId;periodKey=@{value=202609};arguments=$args_}|ConvertTo-Json -Depth 8 -Compress
  $expJson=$te.exp|ConvertTo-Json -Compress
  $x=Call PUT "/api/v1/methodologies/$mid/versions/$vid/tests/GOLD_$($te.case)" @{inputJson=$inputJson;expectedJson=$expJson;tolerance=[decimal]$te.tol}
  "  $code test HTTP $($x.S) $($x.T)"
  $old=$sess; $sess=NewSess 'ms-reviewer' $revPass
  $p=Call POST "/api/v1/methodologies/$mid/versions/$vid/publish" @{changeReason='Land rehearsal: AF import + golden';effectiveFrom='2024-01-01'}
  $sess=$old
  if($p.S -in 200,204){
    $anchor=$id2["$tc/Land_RunningHoursMonthly"]; if(-not $anchor){$anchor=$id2["$tc/Land_ERG_Vsl"]}; if(-not $anchor){$anchor=$id2["$tc/Land_Measure_FuelConsumptionTonn"]}
    if(-not $anchor){$anchor=$id2["$tc/Land_Measure_IgnitionsNumber"]}
    $mj=(@{"$anchor"=$code}|ConvertTo-Json -Compress)
    $nb=0; foreach($o in $outs){ $cid=$id2["$tc/$o"]; $b=Call PUT "/api/v1/methodologies/$mid/bindings/$cid/$o" @{matchJson=$mj;isActive=$true}; if($b.S -lt 300){$nb++} else { "  bind $o -> $($b.S) $($b.T)".Substring(0,200) } }
    "  $code active bindings: $nb of $($outs.Count)"
  }
  $tx=[string]$p.T; if($tx.Length -gt 700){$tx=$tx.Substring(0,700)}
  "$code publish HTTP $($p.S) $tx"
}
