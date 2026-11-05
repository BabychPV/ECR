<#
.SYNOPSIS
    Крок 3 HSE401: довідники. LAND_AIR_SUBSTANCE (+ поля COMPONENT і PHASE; нового довідника речовин немає; display = "<SUBSTANCE_CODE> — <назва>", наявні записи оновлюються ідемпотентно) і
    списки HSE401_* зі словників книги (регіон, суша/море, район, об'єкт, вид діяльності, дозвіл (+GUID), тип обладнання, тип джерела).

.DESCRIPTION
    Ціль: Scratch (EcrHse401Scratch, localhost:5180; охорона в Hse401Stand.Lib.ps1) або Live (-Target Live, див. README).
    Ідемпотентно: наявні довідники/записи не створюються повторно. Для НАЯВНИХ записів LAND_AIR_SUBSTANCE (за кодом, напр. E101)
    дозаповнюються лише порожні COMPONENT і PHASE - жодне інше поле не чіпається, нічого не створюється.
    LAND_REGION / AIR_AREA на свіжому стенді НЕ існують (перевіряється GET /registries) - тому створюються HSE401_REGION/HSE401_AREA.
    Вхід: out/hse401-extract.json (Extract-Hse401.ps1).
    -PreviewOnly: лише GET; план (довідники/поля/записи/заповнення) -> out/hse401-preview.json, розділ "registries".
#>
[CmdletBinding()]
param([string]$ExtractFile,
    [ValidateSet('Scratch', 'Live')][string]$Target = 'Scratch', [string]$BaseUrl, [string]$Database, [string]$ConfirmStand, [string]$CredentialFile,
    [switch]$PreviewOnly, [switch]$AcceptNaePreview)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse401Stand.Lib.ps1')
. (Join-Path $here 'Hse401Json.Lib.ps1')
. (Join-Path $here 'Hse401Rows.Lib.ps1')
. (Join-Path $here '..\land\LandAirSubstances.Lib.ps1')   # Format-LandAirSubstanceDisplay
Set-Hse401Target -Target $Target -BaseUrl $BaseUrl -Database $Database -ConfirmStand $ConfirmStand -CredentialFile $CredentialFile -PreviewOnly:$PreviewOnly -AcceptNaePreview:$AcceptNaePreview
if (-not $ExtractFile) { $ExtractFile = Join-Path (Get-Hse401OutDir) 'hse401-extract.json' }
$preview = Test-Hse401Preview

Connect-Hse
$data = Read-HseJson $ExtractFile
$dict = $data['dictionaries']
$today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')

$plan = [ordered]@{
    registriesToCreate = New-Object System.Collections.Generic.List[string]
    fieldsToAdd        = [ordered]@{}
    entriesToCreate    = [ordered]@{}
    fills              = New-Object System.Collections.Generic.List[object]
    displayFixes       = New-Object System.Collections.Generic.List[object]
    alreadySet        = 0
    filled             = 0
}
$fillErrors = New-Object System.Collections.Generic.List[string]

$existing = @{}
foreach ($r in @((Invoke-HseOk GET '/api/v1/registries').Json)) { $existing[[string]$r.code] = $r }

function Ensure-Registry {
    param([string]$Code, [string]$NameEn, [string]$NameRu, [object[]]$Fields, [string]$Reason)
    if (-not $existing.ContainsKey($Code)) {
        if ($preview) {
            $plan.registriesToCreate.Add($Code)
            $plan.fieldsToAdd[$Code] = @($Fields | ForEach-Object { $_.code })
            return $null
        }
        $c = Invoke-HseOk POST '/api/v1/registries' @{ code = $Code; nameL10n = @{ en = $NameEn; ru = $NameRu }; isTemporal = $false }
        Write-HseLog "довідник $Code створено"
    }
    $d = (Invoke-HseOk GET "/api/v1/registries/$Code/definition").Json
    $have = @(if ($null -ne $d.fields) { $d.fields } else { @() })
    $haveCodes = @($have | ForEach-Object { [string]$_.code })
    $missing = @($Fields | Where-Object { $haveCodes -notcontains $_.code })
    if ($missing.Count -gt 0 -and $preview) {
        $plan.fieldsToAdd[$Code] = @($missing | ForEach-Object { $_.code })
        return $d
    }
    if ($missing.Count -gt 0) {
        $bodies = @()
        $ordinal = 1
        foreach ($h in $have) {
            # GET definition віддає ключовість як isScopeField (RegistryFieldDto: IsScopeField = f.IsKey), збереження приймає isKey (D2, 05.11).
            $hKey = if ($null -ne $h.PSObject.Properties['isKey']) { [bool]$h.isKey } else { [bool]$h.isScopeField }
            $bodies += @{ id = $h.id; code = $h.code; nameL10n = @{ values = $h.nameL10n.values }; dataType = $h.dataType; ordinal = $ordinal++; isRequired = $h.isRequired; isKey = $hKey; lookupRegistryDefId = $h.lookupRegistryDefId; unitId = $h.unitId }
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
    if (-not $existing.ContainsKey($Code)) { return $set }   # PreviewOnly: довідника ще немає
    foreach ($e in @((Invoke-HseOk GET "/api/v1/registries/$Code/entries?asOf=$today").Json)) { $set[([string]$e.code).ToUpperInvariant()] = $e }
    return $set
}

function Test-HseBlank($v) { return ($null -eq $v -or ([string]$v).Trim().Length -eq 0) }

function Set-EntryEmptyFields {
    <# Дозаповнює лише порожні поля $FieldCodes наявного запису значеннями з $It.Values. POST entries з Id: значення ЗЛИВАЮТЬСЯ
       (лише передані поля), назва зливається (порожній values - без змін), батько/рівень передаються як є. Токена конкуренції
       (If-Match/rowVersion) на цьому ендпоінті немає. #>
    param([string]$Code, $Def, $Entry, $It, [string[]]$FieldCodes)
    $wanted = [ordered]@{}
    $detail = (Invoke-HseOk GET "/api/v1/registries/$Code/entries/$($Entry.id)").Json
    $curValues = $null
    if ($null -ne $detail.PSObject.Properties['values']) { $curValues = $detail.values }
    $parentId = $null
    if ($null -ne $detail.PSObject.Properties['parentEntryId']) { $parentId = $detail.parentEntryId }
    foreach ($f in $FieldCodes) {
        if (-not $It.Values.ContainsKey($f)) { continue }
        $newVal = [string]$It.Values[$f]
        if (Test-HseBlank $newVal) { continue }
        $cur = $null
        if ($null -ne $curValues -and $null -ne $curValues.PSObject.Properties[$f]) { $cur = $curValues.PSObject.Properties[$f].Value }
        if (Test-HseBlank $cur) { $wanted[$f] = $newVal } else { $plan.alreadySet++ }
    }
    if ($wanted.Count -eq 0) { return }
    foreach ($f in $wanted.Keys) { $plan.fills.Add([ordered]@{ registry = $Code; code = [string]$Entry.code; field = $f; value = [string]$wanted[$f]; entryId = [long]$Entry.id }) }
    if ($preview) { return }
    $vals = @{}; foreach ($f in $wanted.Keys) { $vals[$f] = $wanted[$f] }
    $body = @{ id = [long]$Entry.id; registryDefId = $Def.id; code = [string]$Entry.code; display = @{ values = @{} }; parentEntryId = $parentId; values = $vals }
    $r = Invoke-Hse POST "/api/v1/registries/$Code/entries" $body
    if ($r.Status -in 200, 201) { $plan.filled += $wanted.Count }
    else { $fillErrors.Add("$Code/$($Entry.code) -> $($r.Status) $(Get-EcrErrorCode $r)") }
}

function Add-Entries {
    param([string]$Code, $Def, [object[]]$Items, [string[]]$FillFields = @())   # Items: @{ Code; En; Ru; Values }
    $byCode = Get-EntryCodes $Code
    $created = 0; $had = 0
    $toCreate = New-Object System.Collections.Generic.List[string]
    foreach ($it in $Items) {
        $key = $it.Code.ToUpperInvariant()
        if ($byCode.ContainsKey($key)) {
            $had++
            if ($FillFields.Count -gt 0) { Set-EntryEmptyFields -Code $Code -Def $Def -Entry $byCode[$key] -It $it -FieldCodes $FillFields }
            continue
        }
        if ($preview) { $toCreate.Add($it.Code); continue }
        $disp = @{ en = $(if ($it.En) { $it.En } else { $it.Code }) }
        if ($it.Ru) { $disp.ru = $it.Ru }
        $body = @{ id = $null; registryDefId = $Def.id; code = $it.Code; display = @{ values = $disp }; parentEntryId = $null; values = $it.Values }
        $r = Invoke-Hse POST "/api/v1/registries/$Code/entries" $body
        if ($r.Status -in 200, 201) { $created++ }
        else { throw "POST entry $Code/$($it.Code) -> $($r.Status) $(Get-EcrErrorCode $r) $($r.Text)" }
    }
    if ($preview) {
        $plan.entriesToCreate[$Code] = $toCreate.ToArray()
        Write-HseLog ("  {0}: [preview] створити записів {1}, вже є {2}" -f $Code, $toCreate.Count, $had)
        return
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
    # Показ "<код> — <назва>" (у Excel колонка містить код; сітка показує entry.display). Ідемпотентно (без подвійного префікса).
    $enName = Format-LandAirSubstanceDisplay -Code $code -Name ([string]$comp.En)
    $ruName = if ([string]$comp.Ru) { Format-LandAirSubstanceDisplay -Code $code -Name ([string]$comp.Ru) } else { '' }
    $items += @{ Code = "E$code"; En = $enName; Ru = $ruName; Values = $vals }
}
Add-Entries 'LAND_AIR_SUBSTANCE' $def $items -FillFields @('COMPONENT', 'PHASE')

# Наявні записи: display (en, ru) без префікса "<SUBSTANCE_CODE> — " -> з префіксом. POST entries з Id: назва ЗЛИВАЄТЬСЯ по мовах
# (передаємо лише змінені мови), values порожні (не чіпаємо), код/батько як є. Новий часовий рядок не створюється: запис довідника
# перейменовується на місці (UpsertRegistryEntryHandler -> entry.Rename). Повторний запуск - 0 змін.
$displayFixed = 0; $displayOk = 0; $displaySkipped = 0
if ($existing.ContainsKey('LAND_AIR_SUBSTANCE')) {
    foreach ($e in @((Get-EntryCodes 'LAND_AIR_SUBSTANCE').Values)) {
        $dd = (Invoke-HseOk GET "/api/v1/registries/LAND_AIR_SUBSTANCE/entries/$($e.id)").Json
        $sc = ''
        if ($null -ne $dd.PSObject.Properties['values'] -and $null -ne $dd.values.PSObject.Properties['SUBSTANCE_CODE']) { $sc = ([string]$dd.values.SUBSTANCE_CODE).Trim() }
        if ($sc -notmatch '^[0-9]{3,4}$') { $displaySkipped++; continue }
        $cur = $dd.displayL10n.values
        $changed = @{}
        foreach ($lang in @('en', 'ru')) {
            $p = $cur.PSObject.Properties[$lang]
            if ($null -eq $p -or (Test-HseBlank $p.Value)) { continue }
            $want = Format-LandAirSubstanceDisplay -Code $sc -Name ([string]$p.Value)
            if ($want -cne ([string]$p.Value).Trim()) { $changed[$lang] = $want }
        }
        if ($changed.Count -eq 0) { $displayOk++; continue }
        $plan.displayFixes.Add([ordered]@{ registry = 'LAND_AIR_SUBSTANCE'; code = [string]$e.code; entryId = [long]$e.id; display = $changed })
        if ($preview) { continue }
        $parentId = $null; if ($null -ne $dd.PSObject.Properties['parentEntryId']) { $parentId = $dd.parentEntryId }
        $body = @{ id = [long]$e.id; registryDefId = $def.id; code = [string]$e.code; display = @{ values = $changed }; parentEntryId = $parentId; values = @{} }
        $r = Invoke-Hse POST '/api/v1/registries/LAND_AIR_SUBSTANCE/entries' $body
        if ($r.Status -in 200, 201) { $displayFixed++ }
        else { $fillErrors.Add("display LAND_AIR_SUBSTANCE/$($e.code) -> $($r.Status) $(Get-EcrErrorCode $r)") }
    }
}
if ($preview) { Write-HseLog ("  LAND_AIR_SUBSTANCE: [preview] display без префікса коду: {0}, вже з префіксом: {1}, пропущено (без SUBSTANCE_CODE): {2}" -f $plan.displayFixes.Count, $displayOk, $displaySkipped) }
else { Write-HseLog ("  LAND_AIR_SUBSTANCE: display оновлено: {0}, вже з префіксом: {1}, пропущено (без SUBSTANCE_CODE): {2}" -f $displayFixed, $displayOk, $displaySkipped) }
if ($preview) { Write-HseLog ("  LAND_AIR_SUBSTANCE: [preview] заповнити COMPONENT/PHASE: {0}, вже заповнено: {1}" -f $plan.fills.Count, $plan.alreadySet) }
else { Write-HseLog ("  LAND_AIR_SUBSTANCE: дозаповнено значень COMPONENT/PHASE: {0}, вже було заповнено: {1}" -f $plan.filled, $plan.alreadySet) }

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
    }
    Add-Entries $regCode $d2 $its
}

if ($fillErrors.Count -gt 0) {
    $fillErrors | ForEach-Object { Write-HseLog "  ПОМИЛКА дозаповнення: $_" }
    if (-not $preview) { throw "Дозаповнення COMPONENT/PHASE: помилок $($fillErrors.Count) (див. вище)." }
}
if ($preview) {
    Save-Hse401PlanSection 'registries' ([ordered]@{
            registriesToCreate = $plan.registriesToCreate.ToArray(); fieldsToAdd = $plan.fieldsToAdd; entriesToCreate = $plan.entriesToCreate
            fills = $plan.fills.ToArray(); displayFixes = $plan.displayFixes.ToArray(); alreadySet = $plan.alreadySet
        })
    Write-HseLog 'Довідники: план записано (PreviewOnly).'
}
else { Write-HseLog 'Довідники готові.' }
