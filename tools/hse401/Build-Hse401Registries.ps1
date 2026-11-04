<#
.SYNOPSIS
    Крок 3 HSE401: довідники scratch-стенда. LAND_AIR_SUBSTANCE (+ поля COMPONENT і PHASE; нового довідника речовин немає) і
    списки HSE401_* зі словників книги (регіон, суша/море, район, об'єкт, вид діяльності, дозвіл (+GUID), тип обладнання, тип джерела).

.DESCRIPTION
    Лише scratch EcrHse401Scratch (localhost:5180; охорона в Hse401Stand.Lib.ps1). Ідемпотентно: наявні довідники/записи не чіпаються.
    LAND_REGION / AIR_AREA на свіжому стенді НЕ існують (перевіряється GET /registries) - тому створюються HSE401_REGION/HSE401_AREA;
    якщо вони з'являться, скрипт використає наявні (див. $Reuse нижче).
    Вхід: out/hse401-extract.json (Extract-Hse401.ps1).
#>
[CmdletBinding()]
param([string]$ExtractFile)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
. (Join-Path $here 'Hse401Rows.Lib.ps1')
if (-not $ExtractFile) { $ExtractFile = Join-Path (Get-Hse401OutDir) 'hse401-extract.json' }

Connect-Hse
$data = Read-HseJson $ExtractFile
$dict = $data['dictionaries']
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')

$existing = @{}
foreach ($r in @((Invoke-HseOk GET '/api/v1/registries').Json)) { $existing[[string]$r.code] = $r }

function Ensure-Registry {
    param([string]$Code, [string]$NameEn, [string]$NameRu, [object[]]$Fields, [string]$Reason)
    if (-not $existing.ContainsKey($Code)) {
        $c = Invoke-HseOk POST '/api/v1/registries' @{ code = $Code; nameL10n = @{ en = $NameEn; ru = $NameRu }; isTemporal = $false }
        Write-HseLog "довідник $Code створено"
    }
    $d = (Invoke-HseOk GET "/api/v1/registries/$Code/definition").Json
    $have = @(if ($null -ne $d.fields) { $d.fields } else { @() })
    $haveCodes = @($have | ForEach-Object { [string]$_.code })
    $missing = @($Fields | Where-Object { $haveCodes -notcontains $_.code })
    if ($missing.Count -gt 0) {
        $bodies = @()
        $ordinal = 1
        foreach ($h in $have) {
            $bodies += @{ id = $h.id; code = $h.code; nameL10n = @{ values = $h.nameL10n.values }; dataType = $h.dataType; ordinal = $ordinal++; isRequired = $h.isRequired; isKey = $h.isKey; lookupRegistryDefId = $h.lookupRegistryDefId; unitId = $h.unitId }
        }
        foreach ($f in $missing) {
            $bodies += @{ id = $null; code = $f.code; nameL10n = @{ values = @{ en = $f.en; ru = $f.ru } }; dataType = 'String'; ordinal = $ordinal++; isRequired = [bool]$f.key; isKey = [bool]$f.key; lookupRegistryDefId = $null; unitId = $null }
        }
        $keys = @(); if ($null -ne $d.PSObject.Properties['keys'] -and $null -ne $d.keys) { $keys = @($d.keys) }
        $rules = @(); if ($null -ne $d.PSObject.Properties['rules'] -and $null -ne $d.rules) { $rules = @($d.rules) }
        $put = Invoke-HseOk PUT "/api/v1/registries/$Code/definition" @{ codeMode = $d.codeMode; fields = $bodies; keys = $keys; rules = $rules; reason = $Reason } @{ 'If-Match' = [string]$d.definitionVersion }
        Write-HseLog "  $Code : поля додано: $(($missing | ForEach-Object { $_.code }) -join ', ')"
        $d = (Invoke-HseOk GET "/api/v1/registries/$Code/definition").Json
    }
    return $d
}

function Get-EntryCodes {
    param([string]$Code)
    $set = @{}
    foreach ($e in @((Invoke-HseOk GET "/api/v1/registries/$Code/entries?asOf=$today").Json)) { $set[([string]$e.code).ToUpperInvariant()] = $e }
    return $set
}

function Add-Entries {
    param([string]$Code, $Def, [object[]]$Items)   # Items: @{ Code; En; Ru; Values }
    $byCode = Get-EntryCodes $Code
    $created = 0; $had = 0
    foreach ($it in $Items) {
        if ($byCode.ContainsKey($it.Code.ToUpperInvariant())) { $had++; continue }
        $disp = @{ en = $(if ($it.En) { $it.En } else { $it.Code }) }
        if ($it.Ru) { $disp.ru = $it.Ru }
        $body = @{ id = $null; registryDefId = $Def.id; code = $it.Code; display = @{ values = $disp }; parentEntryId = $null; values = $it.Values }
        $r = Invoke-Hse POST "/api/v1/registries/$Code/entries" $body
        if ($r.Status -in 200, 201) { $created++ }
        else { throw "POST entry $Code/$($it.Code) -> $($r.Status) $(Get-EcrErrorCode $r) $($r.Text)" }
    }
    Write-HseLog ("  {0}: записів створено {1}, вже було {2}" -f $Code, $created, $had)
}

# ---------- LAND_AIR_SUBSTANCE (+COMPONENT, PHASE) ----------
$subFields = @(
    @{ code = 'SUBSTANCE_CODE'; en = 'Substance code'; ru = 'Код вещества'; key = $true },
    @{ code = 'CHEMICAL_FORMULA'; en = 'Chemical formula'; ru = 'Химическая формула'; key = $false },
    @{ code = 'PHYSICAL_STATE_EN'; en = 'Physical state (EN)'; ru = 'Агрегатное состояние (EN)'; key = $false },
    @{ code = 'PHYSICAL_STATE_RU'; en = 'Physical state (RU)'; ru = 'Агрегатное состояние (RU)'; key = $false },
    @{ code = 'COMPONENT'; en = 'Component (HSE401)'; ru = 'Компонент (HSE401)'; key = $false },
    @{ code = 'PHASE'; en = 'Phase (HSE401)'; ru = 'Агрегатное состояние ЗВ (HSE401)'; key = $false }
)
$def = Ensure-Registry 'LAND_AIR_SUBSTANCE' 'Land: Air pollutant substances' 'Land: загрязняющие вещества (воздух)' $subFields 'HSE401: COMPONENT, PHASE'
$items = @()
foreach ($p in $dict['pollutant']) {
    $code = ([string]$p['code']).Trim()
    $comp = Split-HseBilingual ([string]$p['component'] -replace '(?<=\S)-(?=[А-Яа-яЁё])', ' - ')
    $phs = Split-HseBilingual ([string]$p['phase'] -replace '(?<=\S)-(?=[А-Яа-яЁё])', ' - ')
    $vals = @{ SUBSTANCE_CODE = $code; COMPONENT = [string]$p['component'] }
    if ([string]$p['phase']) { $vals.PHASE = [string]$p['phase'] }
    if ($phs.En) { $vals.PHYSICAL_STATE_EN = $phs.En }
    if ($phs.Ru) { $vals.PHYSICAL_STATE_RU = $phs.Ru }
    $items += @{ Code = "E$code"; En = $comp.En; Ru = $comp.Ru; Values = $vals }
}
Add-Entries 'LAND_AIR_SUBSTANCE' $def $items

# ---------- списки HSE401_* ----------
$lists = @(
    @{ code = 'HSE401_REGION'; key = 'region'; pfx = 'REG'; xl = 'C'; en = 'HSE401: Region'; ru = 'HSE401: Регион'; reuse = 'LAND_REGION' },
    @{ code = 'HSE401_ONOFF'; key = 'onoff'; xl = 'E'; pfx = 'OON'; en = 'HSE401: On/Offshore'; ru = 'HSE401: Суша/Море'; reuse = $null },
    @{ code = 'HSE401_AREA'; key = 'area'; xl = 'F'; pfx = 'ARE'; en = 'HSE401: Area'; ru = 'HSE401: Район работ'; reuse = 'AIR_AREA' },
    @{ code = 'HSE401_FACILITY'; key = 'facility'; xl = 'G'; pfx = 'FAC'; en = 'HSE401: Location / facility'; ru = 'HSE401: Местоположение/Объект'; reuse = $null },
    @{ code = 'HSE401_ACTIVITY'; key = 'activity'; xl = 'H'; pfx = 'ACT'; en = 'HSE401: Type of activity'; ru = 'HSE401: Вид деятельности'; reuse = $null },
    @{ code = 'HSE401_PERMIT'; key = 'permit'; xl = 'I'; pfx = 'PRM'; en = 'HSE401: Permit number'; ru = 'HSE401: Номер разрешения'; reuse = $null },
    @{ code = 'HSE401_EQUIPTYPE'; key = 'equip'; xl = 'J'; pfx = 'EQT'; en = 'HSE401: Equipment type'; ru = 'HSE401: Тип оборудования'; reuse = $null },
    @{ code = 'HSE401_SRCTYPE'; key = 'srctype'; xl = 'K'; pfx = 'SRT'; en = 'HSE401: Emission source type'; ru = 'HSE401: Тип источника эмиссии'; reuse = $null }
)
$map = [ordered]@{}   # ключ словника -> реєстр + мапа "нормалізований рядок" -> код запису
foreach ($l in $lists) {
    $regCode = $l.code
    if ($l.reuse -and $existing.ContainsKey($l.reuse)) { Write-HseLog "  $($l.reuse) існує - але формат ключів інший, HSE401 бере власний $regCode"; }
    $fields = @(
        @{ code = 'SOURCE_TEXT'; en = 'Text as in the workbook'; ru = 'Текст как в книге'; key = $true }
    )
    if ($l.key -eq 'permit') { $fields += @{ code = 'AF_GUID'; en = 'AF GUID'; ru = 'GUID AF'; key = $false } }
    $d2 = Ensure-Registry $regCode $l.en $l.ru $fields 'HSE401: поля'
    $its = @()
    $guids = @($dict['permitGuid'])
    foreach ($pl in (Get-HseListPlan -Extract $data -Key $l.key -Pfx $l.pfx -Xl $l.xl)) {
        $b = Split-HseBilingual $pl.Text
        $vals = @{ SOURCE_TEXT = $pl.Text }
        if ($l.key -eq 'permit' -and -not $pl.IsExtra -and $guids.Count -ge $pl.Index) { $vals.AF_GUID = [string]$guids[$pl.Index - 1] }
        $its += @{ Code = $pl.Code; En = $(if ($b.En) { $b.En } else { $pl.Text }); Ru = $b.Ru; Values = $vals }
    }    Add-Entries $regCode $d2 $its
}
Write-HseLog 'Довідники готові.'
