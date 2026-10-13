<#
.SYNOPSIS
    Будує в ECR повний шаблон Land (усі 20 звітних аркушів Excel-шаблону RDS_xxx_Template_Land_2026_v1.0.4.0)
    і базу для UI-приймальної: довідники, одиниці, шапка Contract, методології AF з рулами/прив'язками,
    проєкт, ролі й користувачі, документи.

.DESCRIPTION
    Джерело структури — docs/delivery/reference-data/land-template/land-template.model.json (генерується
    розбором xlsm; див. README.md поруч). Скрипт ідемпотентний: кожен крок спершу читає стан за кодом
    і створює лише відсутнє; повторний запуск дає 0 змін. «З нуля» = нова порожня база (tools/setup-dev-db.ps1)
    + один запуск цього скрипта.

    Етапи (-Stage): Accounts, Units, Registries, Contract, Template, Project, Methodologies, Documents, All.
      Accounts      ролі й користувачі (паролі генеруються, пишуться у -AccountsFile, не в репозиторій)
      Units         одиниці, яких немає в каталозі (mg/l, kg/h, t/h, thous. m3)
      Registries    довідники з моделі (DropdownList/Configuration) — NAME + display EN/RU
      Contract      довідники вкладки «2. Contract» (tools/land/Import-ContractDictionaries.ps1)
      Template      шаблон LAND_UI: аркуші/таблиці/колонки/рядки/формули/правила; шапка (Apply-ContractHeader.ps1); публікація
      Project       політика періодів, проєкт, ролі-гранти, маршрут погодження, відкриття періодів
      Methodologies імпорт пакета AF, рули, прив'язки виходів, golden-тести, публікація другою особою
      Documents     документи з даними (синтетика + -GoldenDir)
      LandParity    (лише явно, в All не входить) правила категорії C11/C12 з af-land-category-rules.json, склад речовин
                    версії з суфіксів категорій констант, аргументи тест-кейсу PARITY_1 для правила категорії
                    ⚠ Порядок: LandParity виконується ПЕРЕД Methodologies (у одному запуску скрипт сам ставить його
                    першим). Methodologies створює прив'язки колонок результатів LP-08 (вимкнені теж), а перевірка
                    правила категорії звіряє @-аргументи з колонками ВСІХ прив'язок: після Methodologies усі
                    PUT category-rule дають 422 ECR-CALC-0422 categoryRuleUnknownArgument (D1, RC E).
                    Схема: імпорт C11/C12 -> -Stage LandParity -> -Stage Methodologies (повторно, створює прив'язки).
      CompositionMass (лише явно, в All не входить) у формулах ЧЕРНЕТОК C11_01/02/10, C12_01/03 токен @CompositionMass
                    замінюється фрагментом складу по речовині (довідники LAND_HSE400_COMPOSITION/_STREAM/_SUBSTANCE,
                    CURRENT_SUBSTANCE()); потребує довідника LAND_HSE400_SUBSTANCE (Registries або
                    Import-LandHse400Substance.ps1). Чернетку з фрагментом сам переводить у NumericMode=Strict
                    (REG*/CURRENT_SUBSTANCE() у Legacy не публікуються, ECR-CALC-0433); -KeepNumericMode вимикає
                    цей перехід і версії вище за Legacy
      SubstanceApplicability (лише явно, в All не входить) правило категорії ЧЕРНЕТОК із моделі
                    (substanceApplicability.methodologies) обгортається SUBSTANCE_APPLICABLE(...): речовина, якої немає
                    у списку кодів рядка старого C# (потік / InvNum / NAE), для рядка пропускається - кількість рядків
                    результату збігається зі старою системою. Потребує довідника LAND_SUBSTANCE_APPLICABILITY
                    (-Stage Registries) і версії вище за Legacy; повтор - 0 змін; LandParity обгортку не скидає
      MassFraction  (лише явно, в All не входить; -Stage CompositionMass виконує його теж) у формулах ЧЕРНЕТКИ C11_11_04
                    токен @Land_MassFraction замінюється фрагментом if(CURRENT_SUBSTANCE() = '415', @Land_MassFraction_0415,
                    ...) з моделі (argumentBridges.massFraction); версії рівня Legacy ставиться Strict. Довідник
                    LAND_NAE_GROUP (Land_Power, Category1) заводить етап Registries або Import-LandNaeGroup.ps1
      ParityBranch  (лише явно, в All не входить; запускати ПІСЛЯ Methodologies і CompositionMass) у ЧЕРНЕТЦІ ECW_C12_02
                    формули type2 компонентів 301/304/330/337 (ECW_EC_tons|gsec_<код>_type2) обгортаються
                    if(@Land_Measure_Component_<код> = NULL, <вираз>, NULL): за наявного значення компонента виходить лише
                    type1 (як у старому коді), інакше лише type2; код 415 і type1 не чіпаються; аргумент додається до
                    списку формули. Публікації не робить; повтор - 0 змін

    ⛔ Не запускати проти робочої бази замовника. Скрипт нічого не видаляє, окрім явного
    -PruneStaleTables (м'яке видалення таблиць чернетки, яких немає в моделі; без прапора - лише попередження).
    Формули колонок наявної чернетки-клону порівнюються з моделлю й оновлюються (D-YE-1). Прод-БД старої системи тут
    НЕ читається (набір із прода — окремий локальний скрипт, не в репозиторії).

.PARAMETER BaseUrl            Адреса API (за замовчуванням http://localhost:5094).
.PARAMETER ModelPath          land-template.model.json.
.PARAMETER AfPackage          Пакет методологій AF (JSON ecr-methodology-package).
.PARAMETER AccountsFile       Файл «користувач<TAB>пароль<TAB>роль» (створюється/доповнюється; поза репозиторієм).
.PARAMETER BootstrapPassword  Пароль bootstrap, якщо його ще немає в -AccountsFile (SecureString).
.PARAMETER GoldenDir          Каталог golden-*.csv (локальні, поза репозиторієм); без нього тести беруться зі спеки (P1..P5).
.PARAMETER Stage              Етапи.
.PARAMETER PruneStaleTables   LP-28: м'яко видалити з чернетки таблиці, яких немає в моделі (DELETE sheets/{аркуш}/tables/{код};
                              сервер відмовляє, коли у версії є документи). Без прапора - лише попередження зі списком кодів.
                              ⚠ Не на першій чернетці після publish: Prune до migrate-version (tableRenames T9a_03/06/07 -> T9a_17a/21a/21b)
                              блокує перенос документів (dataLoss). Порядок - README, «Порядок етапів стенда» (RC32 F1/F2).
.PARAMETER KnownStaleTables   Коди "АРКУШ/ТАБЛИЦЯ" (або лише код таблиці), які не вважати застарілими.
.PARAMETER KeepNumericMode    -Stage CompositionMass: не переводити чернетки з фрагментом складу в NumericMode=Strict
                              (за замовчуванням переводить: REGFIND/REGFIELD/CURRENT_SUBSTANCE() у Legacy -> ECR-CALC-0433).
.PARAMETER DryRun             Лише читання й підрахунок.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = $(if ($env:ECR_BASE_URL) { $env:ECR_BASE_URL } else { 'http://localhost:5094' }),
    [string]$ModelPath,
    [string]$AfPackage,
    [string]$AccountsFile = 'F:\ecr-backup\land-ui\accounts.local.txt',
    [securestring]$BootstrapPassword,
    [string]$GoldenDir,
    [ValidateSet('Accounts', 'Units', 'Registries', 'Contract', 'Template', 'Project', 'Documents', 'Methodologies', 'SubstanceApplicability', 'ParityBranch', 'Data', 'MassFraction', 'LandParity', 'CompositionMass', 'All')]
    [string[]]$Stage = @('All'),
    [ValidateSet('Sql', 'Api')][string]$StructureMode = 'Sql',
    [string]$SqlConnection = 'Server=localhost;Database=EcrLandUi;Integrated Security=true;TrustServerCertificate=true',
    [string]$TemplateCode = 'LAND_UI',
    [string]$ProjectCode = 'LANDUI2026',
    [int]$Year = 2026,
    [string]$ConfirmStand,
    [switch]$FillData,
    [switch]$PruneStaleTables,
    [switch]$KeepNumericMode,
    [string[]]$KnownStaleTables = @(),
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
$modelFile = $ModelPath
if (-not $modelFile) { $modelFile = Join-Path $repo 'docs\delivery\reference-data\land-template\land-template.model.json' }
$afFile = $AfPackage
if (-not $afFile) { $afFile = Join-Path $repo 'docs\delivery\reference-data\land-template\af-land-package.json' }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'Land-CfRules.ps1')
. (Join-Path $here 'LandTemplateDiff.Lib.ps1')
Set-StrictMode -Off

# Безпека стенда: лише локальні API і SQL (база з іменем EcrLand*); мутації лише з явним
# -ConfirmStand <адреса>/<база> (наприклад localhost:5094/EcrLandUi).
Assert-LocalBaseUrl $BaseUrl
$standDatabase = Assert-LocalSqlConnection $SqlConnection
if (-not $DryRun) { Set-EcrStandConfirmation -Stand $ConfirmStand -Url $BaseUrl -Database $standDatabase }
# Дочірні скрипти (API) отримують лише адресу стенду: база їм невідома й не потрібна.
$childStand = if ($ConfirmStand -and $ConfirmStand.LastIndexOf('/') -gt 0) { $ConfirmStand.Substring(0, $ConfirmStand.LastIndexOf('/')) } else { $ConfirmStand }

# Тимчасові 503 (ECR-SYS-0503 databaseBusy) і 429 повторюємо: стенд ділить SQL Server з іншими сесіями.
${function:Invoke-EcrOnce} = ${function:Invoke-Ecr}
# 429: чекаємо рівно Retry-After (ціле число секунд, мін. 1); без заголовка - паузи 2/4/6/8/10 с. Бюджет очікування
# на один виклик - 180 с (вікно квоти перерахунку 60 с, DocumentRecalculateQuota).
function Get-RetryAfterSeconds($Response) {
    $raw = $null
    if ($Response -and $Response.Headers) {
        foreach ($k in @($Response.Headers.Keys)) { if ([string]$k -ieq 'Retry-After') { $raw = [string]$Response.Headers[$k] } }
    }
    $n = 0
    if ($raw -and [int]::TryParse($raw.Trim(), [ref]$n)) { return [Math]::Max(1, $n) }
    return 0
}
function Invoke-Ecr {
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, [Alias('Body')]$Payload = $null, [hashtable]$Headers = @{})
    $r = $null
    $waited = 0
    foreach ($try in 1..6) {
        $r = Invoke-EcrOnce -Method $Method -Path $Path -Body $Payload -Headers $Headers
        if ($r.Status -ne 503 -and $r.Status -ne 429) { return $r }
        $pause = 2 * $try
        if ($r.Status -eq 429) { $ra = Get-RetryAfterSeconds $r; if ($ra -gt 0) { $pause = $ra } }
        if ($waited + $pause -gt 180) { break }
        $waited += $pause
        Start-Sleep -Seconds $pause
    }
    return $r
}
# Пейсинг масового перерахунку: не частіше 1 виклику на 2.2 с (< 30/хв, межа користувача).
$script:RecalcLastCall = [DateTime]::MinValue
function Wait-RecalcPace {
    $gap = 2.2 - ([DateTime]::UtcNow - $script:RecalcLastCall).TotalSeconds
    if ($gap -gt 0) { Start-Sleep -Milliseconds ([int][Math]::Ceiling($gap * 1000)) }
    $script:RecalcLastCall = [DateTime]::UtcNow
}

function Want([string]$s) { return ($Stage -contains 'All') -or ($Stage -contains $s) }
function Log([string]$m) { Write-Host ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $m) }
function Api {
    param([string]$Method, [string]$Path, $Payload = $null, [hashtable]$Headers = @{}, [int[]]$Ok = @(200, 201, 202, 204))
    $r = Invoke-Ecr -Method $Method -Path $Path -Body $Payload -Headers $Headers
    if ($Ok -notcontains $r.Status) {
        $t = [string]$r.Text
        throw ("{0} {1} -> HTTP {2} {3} {4}" -f $Method, $Path, $r.Status, (Get-EcrErrorCode $r), $t.Substring(0, [Math]::Min(500, $t.Length)))
    }
    return $r
}
function L10n([string]$en, [string]$ru) { $h = @{ en = $en }; if ($ru) { $h.ru = $ru }; return $h }
function NewPassword([string]$prefix) {
    $b = New-Object byte[] 9
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
    return $prefix + '-' + ([Convert]::ToBase64String($b) -replace '[+/=]', 'q') + '-7Zx'
}

# ---------- акаунти ----------
$Acc = @{}
if (Test-Path $AccountsFile) {
    foreach ($ln in Get-Content $AccountsFile) { $p = $ln -split "`t"; if ($p.Count -ge 2) { $Acc[$p[0]] = $p[1] } }
}
function SaveAccount([string]$u, [string]$pw, [string]$role) {
    $Acc[$u] = $pw
    $dir = Split-Path -Parent $AccountsFile
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    $lines = @()
    if (Test-Path $AccountsFile) { $lines = @(Get-Content $AccountsFile | Where-Object { ($_ -split "`t")[0] -ne $u }) }
    $lines += "$u`t$pw`t$role"
    Set-Content -Path $AccountsFile -Value $lines -Encoding ascii
}
function LoginAs([string]$u) {
    if (-not $Acc.ContainsKey($u)) { throw "Немає пароля акаунта $u у $AccountsFile (спершу -Stage Accounts)." }
    Connect-Ecr -BaseUrl $BaseUrl -User $u -Password (ConvertTo-SecureString $Acc[$u] -AsPlainText -Force)
}

# ---------- модель ----------
Log "Модель: $modelFile"
$Mdl = Get-Content -LiteralPath $modelFile -Raw -Encoding UTF8 | ConvertFrom-Json
Log ("Аркушів {0}, таблиць {1}, колонок {2}, довідників {3}" -f $Mdl.stats.reportSheets, $Mdl.stats.tables, $Mdl.stats.columns, $Mdl.stats.registries)
$cellFormulaCount = 0
foreach ($sh in @($Mdl.sheets)) { foreach ($tb in @($sh.tables)) { $cellFormulaCount += @($tb.cellFormulas | Where-Object { $_ }).Count } }
Log ("Формул комірок (Cell, PUT formulas/cell): {0}" -f $cellFormulaCount)
if ($DryRun) { Log 'DryRun: лише розбір моделі; звернень до API немає.'; return }

$S = @{ Registries = @{}; RegEntries = @{}; Units = @{}; TemplateId = $null; VersionId = $null; Tables = @{}; Cols = @{} }

# ==================== Accounts ====================
$RolePerms = @{
    LandDataEntry = @('Document.View', 'Document.Create', 'Document.Export', 'Document.Import', 'Document.Submit', 'Template.View', 'Registry.View', 'Calculation.View')
    LandApprover  = @('Document.View', 'Document.Export', 'Document.Reopen', 'Template.View', 'Registry.View', 'Calculation.View')
    LandRestricted = @('Document.View', 'Document.Create', 'Document.Export', 'Template.View', 'Registry.View')
}
$Users = @(
    @{ u = 'land-owner'; role = 'LandOwner'; pre = 'Own' },
    @{ u = 'land-reviewer'; role = 'LandOwner'; pre = 'Rev' },
    @{ u = 'land-entry'; role = 'LandDataEntry'; pre = 'Ent' },
    @{ u = 'land-approver'; role = 'LandApprover'; pre = 'Apr' },
    @{ u = 'land-viewer'; role = 'Viewer'; pre = 'Vwr' },
    @{ u = 'land-restricted'; role = 'LandRestricted'; pre = 'Rst' }
)
function Stage-Accounts {
    Log '== Accounts =='
    if (-not $Acc.ContainsKey('bootstrap')) {
        if ($null -eq $BootstrapPassword) { throw 'Немає пароля bootstrap: -BootstrapPassword або рядок у -AccountsFile.' }
        $plain = ConvertTo-PlainText $BootstrapPassword
        Connect-Ecr -BaseUrl $BaseUrl -User 'bootstrap' -Password $BootstrapPassword
        $new = NewPassword 'Adm'
        $c = Invoke-Ecr -Method POST -Path '/api/v1/auth/change-password' -Body @{ currentPassword = $plain; newPassword = $new }
        if ($c.Status -in 200, 204) { SaveAccount 'bootstrap' $new 'BootstrapAdministrator' } else { SaveAccount 'bootstrap' $plain 'BootstrapAdministrator' }
    }
    LoginAs 'bootstrap'
    $perms = @((Api GET '/api/v1/permissions').Json | ForEach-Object { [string]$_.code })
    $roles = @{}
    foreach ($r in @((Api GET '/api/v1/roles').Json)) { $roles[[string]$r.code] = $r }
    $defs = @{ LandOwner = $perms }
    foreach ($k in $RolePerms.Keys) { $defs[$k] = @($RolePerms[$k] | Where-Object { $perms -contains $_ }) }
    foreach ($code in $defs.Keys) {
        if ($roles.ContainsKey($code)) { continue }
        $x = Api POST '/api/v1/roles' @{ code = $code; nameL10n = @{ en = $code }; permissionCodes = @($defs[$code]) }
        Log "  роль $code створено"
    }
    foreach ($usr in $Users) {
        $u = $usr.u
        $pw = NewPassword $usr.pre
        $r = Invoke-Ecr -Method POST -Path '/api/v1/users' -Body @{ userName = $u; provider = 'Local'; sid = $null; displayName = $u; initialPassword = $pw; roleCodes = @($usr.role) }
        if ($r.Status -in 200, 201) {
            Connect-Ecr -BaseUrl $BaseUrl -User $u -Password (ConvertTo-SecureString $pw -AsPlainText -Force)
            $final = NewPassword $usr.pre
            $c = Invoke-Ecr -Method POST -Path '/api/v1/auth/change-password' -Body @{ currentPassword = $pw; newPassword = $final }
            if ($c.Status -in 200, 204) { SaveAccount $u $final $usr.role } else { SaveAccount $u $pw $usr.role }
            Log "  користувач $u ($($usr.role)) створено"
            LoginAs 'bootstrap'
        }
        elseif ($r.Status -in 409, 422) { Log "  користувач $u вже є" }
        else { throw "POST users $u -> $($r.Status) $(Get-EcrErrorCode $r)" }
    }
}

# ==================== Units ====================
function Stage-Units {
    Log '== Units =='
    LoginAs 'land-owner'
    $units = @((Api GET '/api/v1/units').Json)
    foreach ($u in $units) { $S.Units[[string]$u.code] = $u }
    foreach ($n in @($Mdl.units.new)) {
        if ($S.Units.ContainsKey($n.code)) { continue }
        $dim = $S.Units[[string]$n.dimensionOf]
        if ($null -eq $dim) { Log "  ! немає розмірності $($n.dimensionOf) для $($n.code)"; continue }
        $r = Invoke-Ecr -Method POST -Path '/api/v1/units' -Body @{ code = $n.code; symbolL10n = @{ en = $n.symbol }; nameL10n = @{ en = $n.nameEn }
                dimensionId = [int]$dim.dimensionId; factorToBase = [decimal]::Parse($n.factor, [Globalization.CultureInfo]::InvariantCulture); offsetToBase = 0 }
        if ($r.Status -in 200, 201) { Log "  одиницю $($n.code) створено" } else { Log "  ! одиниця $($n.code): HTTP $($r.Status) $(Get-EcrErrorCode $r)" }
    }
    $S.Units = @{}
    foreach ($u in @((Api GET '/api/v1/units').Json)) { $S.Units[[string]$u.code] = [int]$u.id }
}
function UnitId([string]$symbolKey) {
    if (-not $symbolKey) { return $null }
    $code = $Mdl.units.map.$symbolKey
    if (-not $code) { return $null }
    if ($S.Units.ContainsKey($code)) { return [int]$S.Units[$code] }
    return $null
}

# ==================== Registries ====================
function Ensure-Registry($reg) {
    $code = [string]$reg.code
    $list = @((Api GET '/api/v1/registries').Json)
    $def = $list | Where-Object { $_.code -eq $code } | Select-Object -First 1
    if ($null -eq $def) {
        $null = Api POST '/api/v1/registries' @{ code = $code; nameL10n = @{ en = [string]$reg.nameEn }; isTemporal = $false }
    }
    $d = (Api GET "/api/v1/registries/$code/definition").Json
    $has = @($d.fields) | Where-Object { $_.code -eq 'NAME' -or ($reg.fieldNames -and $reg.fieldNames -contains $_.code) }
    if (-not $has) {
        $fields = @()
        $keys = @()
        if ($reg.fieldSpecs) {
            # LP-10: довідник із типізованими полями й складеним ключем (LAND_HSE400_*): fieldSpecs + keys із моделі.
            $o = 1
            foreach ($f in @($reg.fieldSpecs)) {
                $fields += @{ id = $null; code = [string]$f.code; nameL10n = @{ values = @{ en = [string]$f.code } }; dataType = [string]$f.dataType; ordinal = $o++
                    isRequired = [bool]$f.isKey; isKey = [bool]$f.isKey; lookupRegistryDefId = $null; unitId = $null }
            }
            foreach ($k in @($reg.keys)) {
                $keys += @{ id = $null; code = [string]$k.code; nameL10n = @{ values = @{ en = [string]$k.code } }; fieldCodes = @($k.fields | ForEach-Object { [string]$_ })
                    isPrimary = [bool]$k.isPrimary; ignoreCase = [bool]$k.ignoreCase; isActive = $true }
            }
        }
        elseif ($reg.fieldNames) {
            $o = 1
            foreach ($f in @($reg.fieldNames)) {
                $fields += @{ id = $null; code = [string]$f; nameL10n = @{ values = @{ en = [string]$f } }; dataType = 'String'; ordinal = $o++
                    isRequired = ([string]$f -eq [string]$reg.keyField); isKey = ([string]$f -eq [string]$reg.keyField); lookupRegistryDefId = $null; unitId = $null }
            }
        }
        else {
            $fields = @(@{ id = $null; code = 'NAME'; nameL10n = @{ values = @{ en = 'Name'; ru = 'Название' } }; dataType = 'String'; ordinal = 1
                    isRequired = $true; isKey = $true; lookupRegistryDefId = $null; unitId = $null })
        }
        $null = Api PUT "/api/v1/registries/$code/definition" @{ codeMode = $d.codeMode; fields = $fields; keys = $keys; rules = @(); reason = 'Land template build' } @{ 'If-Match' = [string]$d.definitionVersion }
        $d = (Api GET "/api/v1/registries/$code/definition").Json
    }
    $today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    $have = @{}
    foreach ($e in @((Api GET "/api/v1/registries/$code/entries?asOf=$today").Json)) { $have[([string]$e.code).ToUpperInvariant()] = $e }
    $created = 0
    foreach ($e in @($reg.entries)) {
        if ($have.ContainsKey(([string]$e.code).ToUpperInvariant())) { continue }
        $disp = @{ en = [string]$e.en }
        if ($e.ru) { $disp.ru = [string]$e.ru }
        $vals = @{}
        if ($reg.fieldNames) {
            # LP-10: поля Decimal/Int (POWER_GROUP довідника LAND_NAE_GROUP) - числом з InvariantCulture, решта - рядком.
            $numeric = @{}
            if ($reg.fieldSpecs) { foreach ($fs in @($reg.fieldSpecs)) { if ([string]$fs.dataType -in 'Decimal', 'Int') { $numeric[[string]$fs.code] = $true } } }
            foreach ($p in $e.fields.PSObject.Properties) {
                if ($numeric.ContainsKey([string]$p.Name)) { $vals[$p.Name] = [decimal]::Parse([string]$p.Value, [Globalization.CultureInfo]::InvariantCulture) }
                else { $vals[$p.Name] = [string]$p.Value }
            }
        }
        else { $vals.NAME = [string]$e.name }
        $r = Invoke-Ecr -Method POST -Path "/api/v1/registries/$code/entries" -Body @{ id = $null; registryDefId = $d.id; code = [string]$e.code; display = @{ values = $disp }; parentEntryId = $null; values = $vals }
        if ($r.Status -in 200, 201) { $created++ } else { Log "  ! $code/$($e.code): HTTP $($r.Status) $(Get-EcrErrorCode $r)" }
    }
    $S.Registries[$code] = [int]$d.id
    return $created
}
function Stage-Registries {
    Log '== Registries =='
    LoginAs 'land-owner'
    $n = 0
    foreach ($reg in @($Mdl.registries)) { $c = Ensure-Registry $reg; $n += $c; if ($c) { Log ("  {0}: +{1}" -f $reg.code, $c) } }
    Log "  нових записів: $n"
}
function Load-Registries {
    $list = @((Api GET '/api/v1/registries').Json)
    foreach ($r in $list) { $S.Registries[[string]$r.code] = [int]$r.id }
}
function RegEntryId([string]$regCode, [string]$name) {
    if (-not $S.RegEntries.ContainsKey($regCode)) {
        $today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
        $h = @{}
        foreach ($e in @((Api GET "/api/v1/registries/$regCode/entries?asOf=$today").Json)) {
            $nm = [string]$e.display
            if (-not $nm) { $nm = [string]$e.code }
            $h[$nm] = [int]$e.id; $h['code:' + $e.code] = [int]$e.id
        }
        $S.RegEntries[$regCode] = $h
    }
    $h = $S.RegEntries[$regCode]
    if ($h.ContainsKey($name)) { return $h[$name] }
    foreach ($k in $h.Keys) { if ($k -like "$name*") { return $h[$k] } }
    $en = ($name -split ' - ', 2)[0]
    if ($en -ne $name -and $h.ContainsKey($en)) { return $h[$en] }
    throw "Немає запису '$name' у довіднику $regCode"
}

# ==================== Contract dictionaries ====================
function Stage-Contract {
    Log '== Contract (довідники вкладки 2. Contract) =='
    $pw = ConvertTo-SecureString $Acc['land-owner'] -AsPlainText -Force
    & (Join-Path $here 'Import-ContractDictionaries.ps1') -BaseUrl $BaseUrl -User 'land-owner' -Password $pw -ConfirmStand $childStand
}

# ==================== Template ====================
$DataTypeCode = @{ String = 0; Int = 1; Decimal = 2; Bool = 3; Date = 4; Lookup = 5; Formula = 6; Unit = 7; Calculated = 8 }
$LayoutCode = @{ MonthsInColumns = 0; MonthsInRows = 1; Static = 2; PerPeriodInstance = 3 }
$RowModeCode = @{ Fixed = 0; Dynamic = 1; Mixed = 2 }
$RowKindCode = @{ Group = 0; Item = 1; Balance = 2; Note = 3; Header = 4 }
$SeverityCode = @{ Info = 0; Warning = 1; Error = 2 }

function Json-L10n([string]$en, [string]$ru) {
    $h = [ordered]@{ en = $en }
    if ($ru) { $h['ru'] = $ru }
    return ($h | ConvertTo-Json -Compress)
}

function Sql-Query($cn, [string]$sql, [hashtable]$p = @{}) {
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 300
    foreach ($k in $p.Keys) { [void]$cmd.Parameters.AddWithValue('@' + $k, $p[$k]) }
    $dt = New-Object System.Data.DataTable
    $da = New-Object System.Data.SqlClient.SqlDataAdapter($cmd)
    [void]$da.Fill($dt)
    return , $dt
}
function Sql-Exec($cn, [string]$sql, [hashtable]$p = @{}) {
    Assert-EcrMutationAllowed 'SQL-запис (Sql-Exec)'
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 300
    foreach ($k in $p.Keys) { [void]$cmd.Parameters.AddWithValue('@' + $k, $p[$k]) }
    return $cmd.ExecuteNonQuery()
}
function Bulk-Insert($cn, [string]$table, $dt) {
    Assert-EcrMutationAllowed "SQL-запис (SqlBulkCopy у $table)"
    if ($dt.Rows.Count -eq 0) { return }
    $bc = New-Object System.Data.SqlClient.SqlBulkCopy($cn)
    $bc.DestinationTableName = $table
    $bc.BulkCopyTimeout = 600
    $bc.BatchSize = 2000
    foreach ($c in $dt.Columns) { [void]$bc.ColumnMappings.Add($c.ColumnName, $c.ColumnName) }
    $bc.WriteToServer($dt)
    $bc.Close()
}

# Швидка гілка заповнення чернетки: SQL-масово (SqlBulkCopy) у cfg.*. Причина: PUT .../columns/{code} через API завантажує
# усю версію й двічі DetectChanges на кожну колонку — O(n) на виклик, для 5 тис. колонок це години (див. gap G-API-1).
# Дані ті самі, що й через API; публікація, шапка, довідники й усе решта — через API.
function Fill-StructureSql([int]$vid) {
    Assert-EcrMutationAllowed 'масове SQL-заповнення структури'
    $null = Assert-LocalSqlConnection $SqlConnection
    Log '  структура: SQL-масово (SqlBulkCopy)'
    $cn = New-Object System.Data.SqlClient.SqlConnection($SqlConnection)
    $cn.Open()
    try {
        $now = (Get-Date).ToUniversalTime()
        $sheetIds = @{}
        foreach ($r in (Sql-Query $cn 'SELECT Id, Code FROM cfg.SheetDef WHERE TemplateVersionId=@v AND IsDeleted=0' @{ v = $vid }).Rows) { $sheetIds[[string]$r.Code] = [int]$r.Id }
        foreach ($sh in @($Mdl.sheets)) {
            if (-not $sheetIds.ContainsKey([string]$sh.code)) {
                $id = Sql-Query $cn 'INSERT INTO cfg.SheetDef (TemplateVersionId, Code, NameL10n, Ordinal, SheetGroup, IsMandatory, IsVisible, IsDeleted) OUTPUT inserted.Id VALUES (@v,@c,@n,@o,NULL,@m,@vis,0)' @{
                    v = $vid; c = [string]$sh.code; n = (Json-L10n $sh.nameEn $null); o = [int]$sh.ordinal; m = [bool]$sh.mandatory; vis = [bool]$sh.visible }
                $sheetIds[[string]$sh.code] = [int]$id.Rows[0].Id
            }
        }
        $tableIds = @{}
        $haveMax = @{}
        foreach ($r in (Sql-Query $cn 'SELECT t.Id, s.Code AS S, t.Code AS T, t.MaxDynamicRows AS Mx FROM cfg.TableDef t JOIN cfg.SheetDef s ON s.Id=t.SheetDefId WHERE s.TemplateVersionId=@v AND t.IsDeleted=0' @{ v = $vid }).Rows) {
            $tableIds["$($r.S)/$($r.T)"] = [int]$r.Id
            $mxv = $null; if ($r.Mx -isnot [DBNull]) { $mxv = [int]$r.Mx }
            $haveMax["$($r.S)/$($r.T)"] = @{ Id = [int]$r.Id; MaxRows = $mxv }
        }
        foreach ($sh in @($Mdl.sheets)) {
            $to = 0
            foreach ($tb in @($sh.tables)) {
                $to++
                $key = "$($sh.code)/$($tb.code)"
                $hs = [DBNull]::Value; $hsid = Style-IdOf $tb.headerStyleRole; if ($null -ne $hsid) { $hs = [int]$hsid }
                if (-not $tableIds.ContainsKey($key)) {
                    $mx = [DBNull]::Value; if ($tb.maxRows) { $mx = [int]$tb.maxRows }
                    $id = Sql-Query $cn 'INSERT INTO cfg.TableDef (SheetDefId, Code, NameL10n, Ordinal, LayoutKind, RowMode, MaxDynamicRows, HeaderStyleId, StorageMode, IsDeleted) OUTPUT inserted.Id VALUES (@s,@c,@n,@o,@l,@r,@mx,@hs,0,0)' @{
                        s = $sheetIds[[string]$sh.code]; c = [string]$tb.code; n = (Json-L10n $tb.nameEn $tb.nameRu); o = $to
                        l = [byte]$LayoutCode[[string]$tb.layout]; r = [byte]$RowModeCode[[string]$tb.rowMode]; mx = $mx; hs = $hs }
                    $tableIds[$key] = [int]$id.Rows[0].Id
                }
                elseif ($hs -isnot [DBNull]) {
                    # шапка таблиці: стиль ролі (TableDef.HeaderStyleId; API для нього немає - лише SQL-гілка стенда)
                    $null = Sql-Exec $cn 'UPDATE cfg.TableDef SET HeaderStyleId=@h WHERE Id=@t AND (HeaderStyleId IS NULL OR HeaderStyleId<>@h)' @{ h = [int]$hs; t = [int]$tableIds[$key] }
                }
            }
        }
        # D-UPG-1: стеля динамічних рядків НАЯВНИХ таблиць клону підтягується до моделі (лише збільшення; Get-LandMaxRowsPlan).
        # Без цього клон 1.0.7.0 лишав T3_00=498/T9_04=184/T9_07=100, і сервер відхиляв рядки понад стелю (ECR-ROW-0409).
        # По одній таблиці, за Id, з тією самою умовою MaxDynamicRows<@m у SQL (ідемпотентно: повтор - 0 змін).
        $wantMax = @()
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) { $wantMax += [pscustomobject]@{ Key = "$($sh.code)/$($tb.code)"; MaxRows = $tb.maxRows } }
        }
        $maxPlan = Get-LandMaxRowsPlan $wantMax $haveMax
        $maxRaised = 0
        foreach ($u in @($maxPlan.Raise)) {
            $maxRaised += [int](Sql-Exec $cn 'UPDATE cfg.TableDef SET MaxDynamicRows=@m WHERE Id=@t AND MaxDynamicRows IS NOT NULL AND MaxDynamicRows<@m' @{ m = [int]$u.To; t = [int]$u.Id })
        }
        Log ("  таблиць MaxDynamicRows піднято: {0}" -f $maxRaised)
        # --- колонки
        $have = @{}
        $haveInfo = @{}
        foreach ($r in (Sql-Query $cn 'SELECT c.Id, c.TableDefId, c.Code, c.DataType, c.Precision, c.Scale FROM cfg.ColumnDef c JOIN cfg.TableDef t ON t.Id=c.TableDefId JOIN cfg.SheetDef s ON s.Id=t.SheetDefId WHERE s.TemplateVersionId=@v' @{ v = $vid }).Rows) {
            $have["$($r.TableDefId)/$($r.Code)"] = $true
            $pr = $null; if ($r.Precision -isnot [DBNull]) { $pr = [int]$r.Precision }
            $sc = $null; if ($r.Scale -isnot [DBNull]) { $sc = [int]$r.Scale }
            $haveInfo["$($r.TableDefId)/$($r.Code)"] = @{ id = [int]$r.Id; type = [int]$r.DataType; precision = $pr; scale = $sc }
        }
        # LP-29: Precision/Scale НАЯВНИХ колонок чернетки-клону підтягуються до значень моделі, але лише РОЗШИРЕННЯМ
        # (Precision не менша, Scale не менша, цілих розрядів Precision-Scale не менше): значення в decimal(34,16) від цього не
        # залежать, а migrate-version саме так і класифікує Widened (без відмови guardedWithData). Звуження й «було без межі»
        # не чіпаються. Правило те саме, що VersionMigrationPlanner.IsWidening.
        $widen = 0
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) {
                $tidW = $tableIds["$($sh.code)/$($tb.code)"]
                foreach ($c in @($tb.columns | Where-Object { $_.precision })) {
                    $hi = $haveInfo["$tidW/$($c.code)"]
                    if ($null -eq $hi) { continue }
                    $p1 = [int]$c.precision; $s1 = $null; if ($c.scale) { $s1 = [int]$c.scale }
                    if ($hi.precision -eq $p1 -and $hi.scale -eq $s1) { continue }
                    if ($null -eq $hi.precision) { continue }                              # було без межі - стало б звуження
                    if ($p1 -lt $hi.precision) { continue }
                    if ($null -ne $s1) {
                        if ($null -eq $hi.scale -or $s1 -lt $hi.scale) { continue }
                        if (($p1 - $s1) -lt ($hi.precision - $hi.scale)) { continue }     # цілих розрядів меншає
                    }
                    elseif ($null -ne $hi.scale) { continue }                              # Scale була, у моделі її немає
                    $newScale = [DBNull]::Value; if ($null -ne $s1) { $newScale = [byte]$s1 }
                    $null = Sql-Exec $cn 'UPDATE cfg.ColumnDef SET Precision=@p, Scale=@s WHERE Id=@i' @{ p = [byte]$p1; s = $newScale; i = $hi.id }
                    $widen++
                }
            }
        }
        if ($widen -gt 0) { Log "  колонок розширено за Precision/Scale (чернетка-клон): $widen" }
        # Колонка чернетки-клону, яку модель тепер веде як Formula/Calculated (7.0: місячні колонки з формулами комірок), а в
        # клоні вона ще Decimal: тип змінюється лише в чернетці (опубліковану версію захищає тригер), інакше PUT formulas/cell
        # дає ECR-TMPL-4227. Лише перехід у обчислюваний тип; решта розбіжностей типів не чіпається.
        $retype = 0
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) {
                $tid0 = $tableIds["$($sh.code)/$($tb.code)"]
                foreach ($c in @($tb.columns | Where-Object { $_.dataType -eq 'Formula' -or $_.dataType -eq 'Calculated' })) {
                    $hi = $haveInfo["$tid0/$($c.code)"]
                    if ($null -eq $hi -or $hi.type -eq [int]$DataTypeCode[[string]$c.dataType]) { continue }
                    if ($hi.type -notin 2, 1) { continue }   # лише Decimal/Int -> Formula/Calculated
                    $null = Sql-Exec $cn 'UPDATE cfg.ColumnDef SET DataType=@t, IsReadOnly=1 WHERE Id=@i' @{ t = [byte]$DataTypeCode[[string]$c.dataType]; i = $hi.id }
                    $retype++
                }
            }
        }
        if ($retype -gt 0) { Log "  колонок переведено в обчислюваний тип (чернетка-клон): $retype" }
        $dt = New-Object System.Data.DataTable
        foreach ($n in 'TableDefId:int', 'Code:string', 'HeaderL10n:string', 'Ordinal:int', 'DataType:byte', 'Precision:byte', 'Scale:byte', 'IsReadOnly:bool', 'IsRequired:bool',
            'IsHidden:bool', 'IsMonthColumn:bool', 'MonthNumber:byte', 'DefaultValue:string', 'LookupRegistryDefId:int', 'UnitId:int', 'IsBusinessKey:bool', 'IsScopeField:bool', 'IsIndexed:bool', 'IsDeleted:bool') {
            $p = $n -split ':'
            $t = switch ($p[1]) { 'int' { [int] } 'byte' { [byte] } 'bool' { [bool] } default { [string] } }
            [void]$dt.Columns.Add($p[0], $t)
        }
        $cnt = 0
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) {
                $tid = $tableIds["$($sh.code)/$($tb.code)"]
                $ord = 0
                foreach ($c in @($tb.columns)) {
                    $ord++
                    if ($have.ContainsKey("$tid/$($c.code)")) { continue }
                    $type = [string]$c.dataType
                    $row = $dt.NewRow()
                    $row['TableDefId'] = $tid; $row['Code'] = [string]$c.code
                    $h = [string]$c.headerEn; if ($h.Length -gt 400) { $h = $h.Substring(0, 400) }
                    $row['HeaderL10n'] = (Json-L10n $h $c.headerRu); $row['Ordinal'] = $ord; $row['DataType'] = [byte]$DataTypeCode[$type]
                    if ($c.precision) { $row['Precision'] = [byte][int]$c.precision }
                    if ($c.scale) { $row['Scale'] = [byte][int]$c.scale }
                    $row['IsReadOnly'] = [bool]($c.readOnly -or $type -in 'Formula', 'Calculated'); $row['IsRequired'] = [bool]$c.required; $row['IsHidden'] = [bool]$c.hidden
                    $im = [bool]($c.month)
                    $row['IsMonthColumn'] = $im
                    if ($im) { $row['MonthNumber'] = [byte][int]$c.month }
                    if ($c.default) { $row['DefaultValue'] = [string]$c.default }
                    if ($type -eq 'Lookup') {
                        if (-not $S.Registries.ContainsKey([string]$c.registry)) { throw "Колонка $($tb.code)/$($c.code): немає довідника $($c.registry)" }
                        $row['LookupRegistryDefId'] = [int]$S.Registries[[string]$c.registry]
                    }
                    $u = UnitId $c.unit
                    if ($null -ne $u) { $row['UnitId'] = [int]$u }
                    $row['IsBusinessKey'] = $false; $row['IsScopeField'] = $false; $row['IsIndexed'] = $false; $row['IsDeleted'] = $false
                    $dt.Rows.Add($row); $cnt++
                }
            }
        }
        Bulk-Insert $cn 'cfg.ColumnDef' $dt
        Log "  колонок вставлено: $cnt"
        # id колонок
        $S.Cols = @{}
        $colId = @{}
        foreach ($r in (Sql-Query $cn 'SELECT c.Id, c.TableDefId, c.Code FROM cfg.ColumnDef c JOIN cfg.TableDef t ON t.Id=c.TableDefId JOIN cfg.SheetDef s ON s.Id=t.SheetDefId WHERE s.TemplateVersionId=@v' @{ v = $vid }).Rows) {
            $colId["$($r.TableDefId)/$($r.Code)"] = [int]$r.Id
        }
        # --- презентація колонок (L1): styleId ролі, widthPx, displayFormat - для нових і наявних колонок; ідемпотентно
        $dp = New-Object System.Data.DataTable
        [void]$dp.Columns.Add('Id', [int]); [void]$dp.Columns.Add('StyleId', [int]); [void]$dp.Columns.Add('DisplayFormat', [string]); [void]$dp.Columns.Add('WidthPx', [int])
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) {
                $tid = $tableIds["$($sh.code)/$($tb.code)"]
                foreach ($c in @($tb.columns)) {
                    $sid = Style-IdOf $c.styleRole
                    $dfmt = Resolve-LandDisplayFormat $c; if ($null -eq $sid -and -not $dfmt -and -not $c.widthPx) { continue }
                    $cid = $colId["$tid/$($c.code)"]
                    if (-not $cid) { continue }
                    $row = $dp.NewRow(); $row['Id'] = [int]$cid
                    if ($null -ne $sid) { $row['StyleId'] = [int]$sid }
                    if ($dfmt) { $row['DisplayFormat'] = [string]$dfmt }
                    if ($c.widthPx) { $row['WidthPx'] = [int]$c.widthPx }
                    $dp.Rows.Add($row)
                }
            }
        }
        if ($dp.Rows.Count -gt 0) {
            # Порціями по 200 рядків: тригер cfg.TR_ColumnDef_Immutable порівнює inserted x deleted без індексів (O(n^2) на один
            # UPDATE): на клоні версії з тисячами колонок одна команда йшла понад 300 с і падала з Execution Timeout.
            $null = Sql-Exec $cn 'CREATE TABLE #CP (N int IDENTITY(1,1) PRIMARY KEY, Id int, StyleId int NULL, DisplayFormat nvarchar(50) NULL, WidthPx int NULL)'
            $bc = New-Object System.Data.SqlClient.SqlBulkCopy($cn); $bc.DestinationTableName = '#CP'
            foreach ($n in 'Id', 'StyleId', 'DisplayFormat', 'WidthPx') { [void]$bc.ColumnMappings.Add($n, $n) }
            $bc.WriteToServer($dp); $bc.Close()
            $np = 0
            for ($lo = 0; $lo -lt $dp.Rows.Count; $lo += 200) {
                $np += Sql-Exec $cn 'UPDATE c SET StyleId = ISNULL(p.StyleId, c.StyleId), DisplayFormat = ISNULL(p.DisplayFormat, c.DisplayFormat), WidthPx = ISNULL(p.WidthPx, c.WidthPx) FROM cfg.ColumnDef c JOIN #CP p ON p.Id = c.Id WHERE p.N > @lo AND p.N <= @hi AND ((p.StyleId IS NOT NULL AND ISNULL(c.StyleId, -1) <> p.StyleId) OR (p.DisplayFormat IS NOT NULL AND ISNULL(c.DisplayFormat, N'''') <> p.DisplayFormat) OR (p.WidthPx IS NOT NULL AND ISNULL(c.WidthPx, -1) <> p.WidthPx))' @{ lo = $lo; hi = ($lo + 200) }
            }
            $null = Sql-Exec $cn 'DROP TABLE #CP'
            Log "  презентація колонок (стиль/ширина/формат): оновлено $np із $($dp.Rows.Count)"
        }
        # --- рядки
        $haveRows = @{}
        foreach ($r in (Sql-Query $cn 'SELECT r.TableDefId, r.RowKey FROM cfg.RowDef r JOIN cfg.TableDef t ON t.Id=r.TableDefId JOIN cfg.SheetDef s ON s.Id=t.SheetDefId WHERE s.TemplateVersionId=@v' @{ v = $vid }).Rows) {
            $haveRows["$($r.TableDefId)/$($r.RowKey)"] = $true
        }
        $dr = New-Object System.Data.DataTable
        foreach ($n in 'TableDefId:int', 'RowKey:string', 'Ordinal:int', 'LabelL10n:string', 'RowKind:byte', 'IsReadOnly:bool', 'IsDeleted:bool') {
            $p = $n -split ':'
            $t = switch ($p[1]) { 'int' { [int] } 'byte' { [byte] } 'bool' { [bool] } default { [string] } }
            [void]$dr.Columns.Add($p[0], $t)
        }
        $nr = 0
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) {
                $tid = $tableIds["$($sh.code)/$($tb.code)"]
                $o = 0
                foreach ($rw in @($tb.rows)) {
                    $o++
                    if ($haveRows.ContainsKey("$tid/$($rw.key)")) { continue }
                    $row = $dr.NewRow()
                    $row['TableDefId'] = $tid; $row['RowKey'] = [string]$rw.key; $row['Ordinal'] = $o
                    $lab = [string]$rw.en; if ($lab.Length -gt 400) { $lab = $lab.Substring(0, 400) }
                    $row['LabelL10n'] = (Json-L10n $lab $rw.ru); $row['RowKind'] = [byte]$RowKindCode[[string]$rw.kind]; $row['IsReadOnly'] = $false; $row['IsDeleted'] = $false
                    $dr.Rows.Add($row); $nr++
                }
            }
        }
        Bulk-Insert $cn 'cfg.RowDef' $dr
        Log "  рядків вставлено: $nr"
        # батьківські рядки
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) {
                $tid = $tableIds["$($sh.code)/$($tb.code)"]
                $pairs = @(@($tb.rows) | Where-Object { $_.parent })
                if ($pairs.Count -eq 0) { continue }
                $map = @{}
                foreach ($r in (Sql-Query $cn 'SELECT Id, RowKey FROM cfg.RowDef WHERE TableDefId=@t' @{ t = $tid }).Rows) { $map[[string]$r.RowKey] = [int]$r.Id }
                $upd = New-Object System.Data.DataTable
                [void]$upd.Columns.Add('Id', [int]); [void]$upd.Columns.Add('ParentId', [int])
                foreach ($rw in $pairs) {
                    if ($map.ContainsKey([string]$rw.key) -and $map.ContainsKey([string]$rw.parent)) { [void]$upd.Rows.Add($map[[string]$rw.key], $map[[string]$rw.parent]) }
                }
                if ($upd.Rows.Count -gt 0) {
                    $null = Sql-Exec $cn 'CREATE TABLE #P (Id int, ParentId int)'
                    $bc = New-Object System.Data.SqlClient.SqlBulkCopy($cn); $bc.DestinationTableName = '#P'
                    [void]$bc.ColumnMappings.Add('Id', 'Id'); [void]$bc.ColumnMappings.Add('ParentId', 'ParentId')
                    $bc.WriteToServer($upd); $bc.Close()
                    $null = Sql-Exec $cn 'UPDATE r SET ParentRowDefId = p.ParentId FROM cfg.RowDef r JOIN #P p ON p.Id = r.Id; DROP TABLE #P'
                }
            }
        }
        # --- формули колонок
        # D-YE-1: наявні формули читаються з виразом і діалектом - вираз колонки клону порівнюється з моделлю й оновлюється
        # (раніше відсутні лише вставлялися, і старий вираз клону лишався: T5_08/T5_09 Winter `< 4 OR > 9`).
        $haveF = @{}
        foreach ($r in (Sql-Query $cn 'SELECT f.Id, f.TableDefId, f.ColumnDefId, f.Dialect, f.Expression FROM cfg.FormulaDef f JOIN cfg.TableDef t ON t.Id=f.TableDefId JOIN cfg.SheetDef s ON s.Id=t.SheetDefId WHERE s.TemplateVersionId=@v AND f.IsDeleted=0 AND f.Scope=0 AND f.ColumnDefId IS NOT NULL ORDER BY f.Id' @{ v = $vid }).Rows) {
            $fk = "$($r.TableDefId)/$($r.ColumnDefId)"
            if (-not $haveF.ContainsKey($fk)) { $haveF[$fk] = @{ Id = [int]$r.Id; Dialect = [int]$r.Dialect; Expression = [string]$r.Expression } }
        }
        $df = New-Object System.Data.DataTable
        foreach ($n in 'TableDefId:int', 'Scope:byte', 'ColumnDefId:int', 'Dialect:byte', 'Expression:string', 'EvaluationOrder:int', 'IsCrossSheet:bool', 'IsSnapshot:bool', 'IsDeleted:bool') {
            $p = $n -split ':'
            $t = switch ($p[1]) { 'int' { [int] } 'byte' { [byte] } 'bool' { [bool] } default { [string] } }
            [void]$df.Columns.Add($p[0], $t)
        }
        $nf = 0
        $wantF = @(); $fCodes = @{}
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) {
                $tid = $tableIds["$($sh.code)/$($tb.code)"]
                foreach ($c in @($tb.columns | Where-Object { $_.expression })) {
                    $cid = $colId["$tid/$($c.code)"]
                    $fk = "$tid/$cid"
                    $wantF += [pscustomobject]@{ Key = $fk; Expression = [string]$c.expression; Table = [int]$tid; Column = [int]$cid }
                    $fCodes[$fk] = "$($sh.code)/$($tb.code).$($c.code)"
                }
            }
        }
        $fPlan = Get-LandFormulaPlan $wantF $haveF
        foreach ($w in @($fPlan.Insert)) {
            $row = $df.NewRow()
            $row['TableDefId'] = $w.Table; $row['Scope'] = 0; $row['ColumnDefId'] = $w.Column; $row['Dialect'] = 0; $row['Expression'] = [string]$w.Expression
            $row['EvaluationOrder'] = 0; $row['IsCrossSheet'] = [bool]([string]$w.Expression -match '\]\.\[.*\]\.\[');  $row['IsSnapshot'] = $false; $row['IsDeleted'] = $false
            $df.Rows.Add($row); $nf++
        }
        Bulk-Insert $cn 'cfg.FormulaDef' $df
        Log "  формул колонок вставлено: $nf"
        # Оновлення виразу/діалекту наявних формул чернетки порціями по 200 (тригери cfg.TR_*_Immutable порівнюють
        # inserted x deleted без індексів: масове UPDATE одним запитом - O(n^2), урок land-build-clone-fixes).
        $nfu = 0
        $fUpd = @($fPlan.Update)
        if ($fUpd.Count -gt 0) {
            $du = New-Object System.Data.DataTable
            [void]$du.Columns.Add('Id', [int]); [void]$du.Columns.Add('Expression', [string]); [void]$du.Columns.Add('IsCrossSheet', [bool])
            foreach ($u in $fUpd) { [void]$du.Rows.Add([int]$u.Id, [string]$u.Expression, [bool]([string]$u.Expression -match '\]\.\[.*\]\.\[')) }
            $null = Sql-Exec $cn 'CREATE TABLE #FU (N int IDENTITY(1,1) PRIMARY KEY, Id int, Expression nvarchar(max), IsCrossSheet bit)'
            Assert-EcrMutationAllowed 'SQL-запис (SqlBulkCopy у #FU)'
            $bc = New-Object System.Data.SqlClient.SqlBulkCopy($cn); $bc.DestinationTableName = '#FU'
            foreach ($n in 'Id', 'Expression', 'IsCrossSheet') { [void]$bc.ColumnMappings.Add($n, $n) }
            $bc.WriteToServer($du); $bc.Close()
            for ($lo = 0; $lo -lt $du.Rows.Count; $lo += 200) {
                $nfu += Sql-Exec $cn 'UPDATE f SET Expression = p.Expression, Dialect = 0, IsCrossSheet = p.IsCrossSheet FROM cfg.FormulaDef f JOIN #FU p ON p.Id = f.Id WHERE p.N > @lo AND p.N <= @hi' @{ lo = $lo; hi = ($lo + 200) }
            }
            $null = Sql-Exec $cn 'DROP TABLE #FU'
            $names = @($fUpd | ForEach-Object { $fCodes[[string]$_.Key] } | Select-Object -First 30)
            Log ("  формул колонок оновлено (вираз/діалект відрізнявся від моделі): {0}; колонки: {1}{2}" -f $nfu, ($names -join ', '), $(if ($fUpd.Count -gt 30) { ' ...' } else { '' }))
        }
        else { Log '  формул колонок оновлено: 0' }
        # readOnly: колонка моделі Formula/Calculated (або readOnly) у клоні мусить бути лише для читання.
        $nro = 0
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) {
                $tid = $tableIds["$($sh.code)/$($tb.code)"]
                foreach ($c in @($tb.columns | Where-Object { $_.readOnly -or [string]$_.dataType -in 'Formula', 'Calculated' })) {
                    $cid = $colId["$tid/$($c.code)"]
                    if (-not $cid) { continue }
                    $nro += Sql-Exec $cn 'UPDATE cfg.ColumnDef SET IsReadOnly=1 WHERE Id=@i AND IsReadOnly=0' @{ i = [int]$cid }
                }
            }
        }
        if ($nro -gt 0) { Log "  колонок позначено лише для читання (за моделлю): $nro" }
        # --- правила валідації
        $haveV = @{}
        foreach ($r in (Sql-Query $cn 'SELECT v.TableDefId, v.Code FROM cfg.ValidationRule v JOIN cfg.TableDef t ON t.Id=v.TableDefId JOIN cfg.SheetDef s ON s.Id=t.SheetDefId WHERE s.TemplateVersionId=@v' @{ v = $vid }).Rows) {
            $haveV["$($r.TableDefId)/$($r.Code)"] = $true
        }
        $dv = New-Object System.Data.DataTable
        foreach ($n in 'TableDefId:int', 'Code:string', 'Severity:byte', 'Scope:byte', 'ColumnDefId:int', 'Expression:string', 'MessageL10n:string', 'IsActive:bool') {
            $p = $n -split ':'
            $t = switch ($p[1]) { 'int' { [int] } 'byte' { [byte] } 'bool' { [bool] } default { [string] } }
            [void]$dv.Columns.Add($p[0], $t)
        }
        $nv = 0
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) {
                $tid = $tableIds["$($sh.code)/$($tb.code)"]
                foreach ($ru in @($tb.rules)) {
                    if ($haveV.ContainsKey("$tid/$($ru.code)")) { continue }
                    $row = $dv.NewRow()
                    $row['TableDefId'] = $tid; $row['Code'] = [string]$ru.code; $row['Severity'] = [byte]$SeverityCode[[string]$ru.severity]; $row['Scope'] = [byte][int]$ru.scope
                    $cid = $colId["$tid/$($ru.column)"]
                    if ($cid) { $row['ColumnDefId'] = $cid }
                    $row['Expression'] = [string]$ru.expression; $row['MessageL10n'] = (Json-L10n ([string]$ru.message) $null); $row['IsActive'] = $true
                    $dv.Rows.Add($row); $nv++
                }
            }
        }
        Bulk-Insert $cn 'cfg.ValidationRule' $dv
        Log "  правил валідації вставлено: $nv"
        foreach ($k in $tableIds.Keys) { $S.Tables[$k] = $tableIds[$k] }
    }
    finally { $cn.Close() }
}

# D1: формули РІВНЯ КОМІРКИ (PUT …/formulas/cell/{rowKey}/{columnDefId}). Модель несе їх у таблиці як cellFormulas
# ({row, column, expression}); для 7.0 Water consolidation це 18 рядків × 12 місяців = 216 перехресних формул
# виду SUM(a,b,c)/1000 (LP-31: null-безпечно, порожній операнд = 0 як в Excel) з аркуша 7. Water Report (багато-до-багатьох: коди 7009016/17/18 живлять кілька цілей).
# Працює в обох режимах -StructureMode (Sql і Api): ідентифікатори таблиць/колонок беруться зі структури версії.
# Ідемпотентно: PUT за адресою комірки змінює наявну формулу, а не заводить другу.
# LP-28: таблиці чернетки-клону, яких немає в моделі (T9a_03/06/07 після переходу на T9a_17a/21a/21b). Без прапора
# -PruneStaleTables - лише попередження зі списком кодів (нічого не видаляється). З прапором - DELETE
# /template-versions/{vid}/sheets/{аркуш}/tables/{код} (м'яке видалення в чернетці; сервер відмовляє, коли у версії є
# документи - тоді таблиця лишається й про це буде рядок у журналі). Опублікована версія не змінюється: сюди
# потрапляє лише чернетка. Таблиці з кодами моделі й -KnownStaleTables не чіпаються.
function Invoke-StaleTables([int]$vid) {
    $st = (Api GET "/api/v1/template-versions/$vid/structure").Json
    $stale = @(Get-LandStaleTables $st $Mdl $KnownStaleTables)
    if ($stale.Count -eq 0) { Log '  таблиць, яких немає в моделі: 0'; return 0 }
    Log ("  ПОПЕРЕДЖЕННЯ: таблиць у чернетці, яких немає в моделі: {0}: {1}" -f $stale.Count, (($stale | ForEach-Object { $_.Key }) -join ', '))
    if (-not $PruneStaleTables) { Log '  Нічого не видалено; для м''якого видалення запустіть з -PruneStaleTables (або додайте код у -KnownStaleTables).'; return 0 }
    $done = 0
    foreach ($s in $stale) {
        $r = Invoke-Ecr -Method DELETE -Path "/api/v1/template-versions/$vid/sheets/$($s.Sheet)/tables/$($s.Table)"
        if ($r.Status -in 200, 204) { $done++; Log "  таблицю $($s.Key) видалено (м'яко)"; continue }
        Log ("  таблицю {0} НЕ видалено: HTTP {1} {2}" -f $s.Key, $r.Status, (Get-EcrErrorCode $r))
    }
    Log "  застарілих таблиць видалено: $done із $($stale.Count)"
    return $done
}

function Put-CellFormulas([int]$vid) {
    $planned = @()
    foreach ($sh in @($Mdl.sheets)) {
        foreach ($tb in @($sh.tables)) {
            foreach ($cf in @($tb.cellFormulas)) {
                if ($null -eq $cf) { continue }
                $planned += [pscustomobject]@{ Sheet = [string]$sh.code; Table = [string]$tb.code; Row = [string]$cf.row; Column = [string]$cf.column; Expr = [string]$cf.expression }
            }
        }
    }
    if ($planned.Count -eq 0) { Log '  формул комірок у моделі немає'; return 0 }
    $st = (Api GET "/api/v1/template-versions/$vid/structure").Json
    $ids = @{}
    foreach ($s in @($st.sheets)) {
        foreach ($t in @($s.tables)) {
            $cols = @{}
            foreach ($c in @($t.columns)) { $cols[[string]$c.code] = [int]$c.id }
            $ids["$($s.code)/$($t.code)"] = @{ Id = [int]$t.id; Cols = $cols }
        }
    }
    $n = 0
    foreach ($p in $planned) {
        $tb = $ids["$($p.Sheet)/$($p.Table)"]
        if ($null -eq $tb) { throw "Формула комірки $($p.Row)/$($p.Column): немає таблиці $($p.Sheet)/$($p.Table) у структурі версії $vid." }
        if (-not $tb.Cols.ContainsKey($p.Column)) { throw "Формула комірки $($p.Row)/$($p.Column): немає колонки в $($p.Sheet)/$($p.Table)." }
        $cellUrl = "/api/v1/template-versions/$vid/tables/$($tb.Id)/formulas/cell/$([uri]::EscapeDataString($p.Row))/$($tb.Cols[$p.Column])"
        $null = Api PUT $cellUrl @{ dialect = 'Template'; expression = $p.Expr }
        $n++
    }
    Log "  формул комірок записано: $n"
    return $n
}

# L1 (CELL-STYLE-PLAN-2026-10-12): стилі за ролями Excel -> cfg.StyleDef через PUT .../styles/{code} (контракт SaveStyleDefCommand).
# Заводяться лише стилі, на які посилаються колонки (styleRole) і таблиці (headerStyleRole). Upsert за кодом: повтор = 0 змін.
# Старі моделі без ключа `styles` проходять без змін (стилів немає - styleId лишається null, як було).
function Ensure-Styles([int]$vid) {
    $S.StyleIds = @{}
    $used = @{}
    foreach ($sh in @($Mdl.sheets)) {
        foreach ($tb in @($sh.tables)) {
            if ($tb.headerStyleRole) { $used[[string]$tb.headerStyleRole] = $true }
            foreach ($c in @($tb.columns)) { if ($c.styleRole) { $used[[string]$c.styleRole] = $true } }
        }
    }
    $n = 0
    foreach ($st in @($Mdl.styles)) {
        if ($null -eq $st -or -not $used.ContainsKey([string]$st.code)) { continue }
        $body = @{
            fontName = $null; fontSize = $null; isBold = [bool]$st.isBold; isItalic = [bool]$st.isItalic
            foregroundArgb = $(if ($null -ne $st.foregroundArgb) { [int]$st.foregroundArgb } else { $null })
            backgroundArgb = $(if ($null -ne $st.backgroundArgb) { [int]$st.backgroundArgb } else { $null })
            borderJson = $null
            horizontalAlign = $(if ($null -ne $st.horizontalAlign) { [int]$st.horizontalAlign } else { $null })
            verticalAlign = $(if ($null -ne $st.verticalAlign) { [int]$st.verticalAlign } else { $null })
            wrapText = [bool]$st.wrapText; numberFormat = $null
        }
        $r = Api PUT "/api/v1/template-versions/$vid/styles/$($st.code)" $body
        $S.StyleIds[[string]$st.code] = [int]$r.Json.id
        $n++
    }
    Log "  стилів ролей Excel: $n"
}
function Style-IdOf($role) {
    if ($role -and $S.StyleIds -and $S.StyleIds.ContainsKey([string]$role)) { return [int]$S.StyleIds[[string]$role] }
    return $null
}

# L2 плану стилів: шапка таблиці через PATCH .../presentation ([{entityType:'TableDef', entityId, field:'HeaderStyleId', value}]).
# У режимі Sql HeaderStyleId уже записано напряму (Fill-StructureSql), тож PATCH потрібен лише для -StructureMode Api.
# Поле HeaderStyleId у білому списку презентаційного патча з'являється з lane cellstyle-l2-2; без нього сервер відповість
# 422 (presentationFieldUnknown) - це попередження, а не відмова побудови: решта шаблону вже опублікована.
function Set-TableHeaderStyles {
    if ($StructureMode -ne 'Api') { return }
    $changes = @()
    foreach ($sh in @($Mdl.sheets)) {
        foreach ($tb in @($sh.tables)) {
            $sid = Style-IdOf $tb.headerStyleRole
            $tid = $S.Tables["$($sh.code)/$($tb.code)"]
            if ($null -ne $sid -and $tid) { $changes += @{ entityType = 'TableDef'; entityId = [int]$tid; field = 'HeaderStyleId'; value = [string]$sid } }
        }
    }
    if ($changes.Count -eq 0) { return }
    if ($changes.Count -eq 1) { $changes += $changes[0] }   # одиночний елемент конвеєр ConvertTo-Json розгорнув би в об'єкт, а не масив
    $r = Invoke-Ecr -Method PATCH -Path "/api/v1/template-versions/$($S.VersionId)/presentation" -Body $changes
    if ($r.Status -ne 200) {
        Log ("  ПОПЕРЕДЖЕННЯ: стиль шапок таблиць не застосовано (HTTP {0}); потрібна lane cellstyle-l2-2. {1}" -f $r.Status, ([string]$r.Text).Substring(0, [Math]::Min(200, ([string]$r.Text).Length)))
    }
    else { Log "  стиль шапок таблиць: $($changes.Count) змін (PATCH presentation)" }
}

function Stage-Template {
    Log '== Template =='
    LoginAs 'land-owner'
    Load-Registries
    $S.Units = @{}
    foreach ($u in @((Api GET '/api/v1/units').Json)) { $S.Units[[string]$u.code] = [int]$u.id }

    $t = (Api GET ('/api/v1/templates?limit=50&q=' + $TemplateCode)).Json
    $tpl = @($t.items) | Where-Object { $_.code -eq $TemplateCode } | Select-Object -First 1
    if ($null -eq $tpl) {
        $c = Api POST '/api/v1/templates' @{ code = $TemplateCode; nameL10n = @{ en = [string]$Mdl.templateNameEn } }
        $t = (Api GET ('/api/v1/templates?limit=50&q=' + $TemplateCode)).Json
        $tpl = @($t.items) | Where-Object { $_.code -eq $TemplateCode } | Select-Object -First 1
    }
    $S.TemplateId = [int]$tpl.id
    $vers = @((Api GET "/api/v1/templates/$($tpl.id)/versions?limit=100").Json.items)
    $pub = $vers | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1
    $draft = $vers | Where-Object { $_.status -eq 'Draft' } | Sort-Object id -Descending | Select-Object -First 1
    if ($null -ne $pub -and $null -eq $draft) {
        $S.VersionId = [int]$pub.id
        Log "  шаблон $TemplateCode вже опублікований (версія $($pub.version), id $($pub.id)); структуру не чіпаю."
        return
    }
    if ($null -eq $draft) {
        $null = Api POST "/api/v1/templates/$($tpl.id)/versions" @{ versionNumber = [string]$Mdl.versionNumber; cloneFromVersionId = $null }
        $vers = @((Api GET "/api/v1/templates/$($tpl.id)/versions?limit=100").Json.items)
        $draft = $vers | Where-Object { $_.status -eq 'Draft' } | Sort-Object id -Descending | Select-Object -First 1
    }
    $vid = [int]$draft.id
    $S.VersionId = $vid
    Log "  чернетка версії $($draft.version) (id $vid)"
    Ensure-Styles $vid

    $nCols = 0; $nRows = 0; $nRules = 0; $nF = 0
    $apiSheets = @($Mdl.sheets)
    if ($StructureMode -eq 'Sql') { Fill-StructureSql $vid; $apiSheets = @() }   # API-гілку нижче не виконуємо
    foreach ($sh in $apiSheets) {
        $null = Api PUT "/api/v1/template-versions/$vid/sheets/$($sh.code)" @{ nameL10n = (L10n $sh.nameEn $null); ordinal = [int]$sh.ordinal; sheetGroup = $null; isMandatory = [bool]$sh.mandatory; isVisible = [bool]$sh.visible }
        $to = 0
        foreach ($tb in @($sh.tables)) {
            $to++
            $maxRows = $null; if ($tb.maxRows) { $maxRows = [int]$tb.maxRows }
            $tr = (Api PUT "/api/v1/template-versions/$vid/sheets/$($sh.code)/tables/$($tb.code)" @{
                    nameL10n = (L10n $tb.nameEn $tb.nameRu); ordinal = $to; layoutKind = [string]$tb.layout; rowMode = [string]$tb.rowMode; maxDynamicRows = $maxRows }).Json
            $tid = if ($tr.id) { [int]$tr.id } else { [int]$tr.tableDefId }
            $key = "$($sh.code)/$($tb.code)"
            $S.Tables[$key] = $tid
            $S.Cols[$key] = @{}
            $ord = 0
            $formulas = @()
            foreach ($c in @($tb.columns)) {
                $ord++
                $type = [string]$c.dataType
                $lookup = $null
                if ($type -eq 'Lookup') {
                    if (-not $S.Registries.ContainsKey([string]$c.registry)) { throw "Колонка $key/$($c.code): немає довідника $($c.registry)" }
                    $lookup = [int]$S.Registries[[string]$c.registry]
                }
                $isMonth = [bool]($c.month)
                $body = @{
                    headerL10n = (L10n ([string]$c.headerEn).Substring(0, [Math]::Min(250, ([string]$c.headerEn).Length)) $c.headerRu)
                    ordinal = $ord; dataType = $type; isRequired = [bool]$c.required; isReadOnly = [bool]($c.readOnly -or $type -in 'Formula', 'Calculated')
                    isHidden = [bool]$c.hidden; precision = $(if ($c.precision) { [int]$c.precision } else { $null }); scale = $(if ($c.scale) { [int]$c.scale } else { $null })
                    defaultValue = $(if ($c.default) { [string]$c.default } else { $null }); displayFormat = (Resolve-LandDisplayFormat $c); styleId = (Style-IdOf $c.styleRole)
                    lookupRegistryDefId = $lookup; lookupFilter = $null; unitId = (UnitId $c.unit); widthPx = $(if ($c.widthPx) { [int]$c.widthPx } else { $null })
                    isMonthColumn = $isMonth; monthNumber = $(if ($isMonth) { [int]$c.month } else { $null })
                }
                if ($c.header_hint) { }
                $cr = Api PUT "/api/v1/template-versions/$vid/tables/$tid/columns/$($c.code)" $body
                $cid = if ($cr.Json.id) { [int]$cr.Json.id } else { [int]$cr.Json.columnDefId }
                $S.Cols[$key][[string]$c.code] = $cid
                $nCols++
                if ($c.expression) { $formulas += @{ id = $cid; expr = [string]$c.expression; code = [string]$c.code } }
            }
            foreach ($f in $formulas) {
                $null = Api PUT "/api/v1/template-versions/$vid/tables/$tid/formulas/column/$($f.id)" @{ dialect = 'Template'; expression = $f.expr }
                $nF++
            }
            $ro = 0
            foreach ($rw in @($tb.rows)) {
                $ro++
                $null = Api PUT "/api/v1/template-versions/$vid/tables/$tid/rows/$($rw.key)" @{
                    labelL10n = (L10n ([string]$rw.en) $rw.ru); ordinal = $ro; rowKind = [string]$rw.kind; parentRowKey = $(if ($rw.parent) { [string]$rw.parent } else { $null }); isReadOnly = $false }
                $nRows++
            }
            foreach ($ru in @($tb.rules)) {
                $cid = $S.Cols[$key][[string]$ru.column]
                $null = Api PUT "/api/v1/template-versions/$vid/tables/$tid/validation-rules/$($ru.code)" @{
                    severity = [string]$ru.severity; scope = [int]$ru.scope; expression = [string]$ru.expression; messageL10n = @{ en = [string]$ru.message }; columnDefId = $cid; isActive = $true }
                $nRules++
            }
        }
        Log ("  аркуш {0}: таблиць {1}" -f $sh.code, @($sh.tables).Count)
    }
    Log "  колонок $nCols, формул $nF, рядків $nRows, правил $nRules"
    $nCell = Put-CellFormulas $vid
    $nStale = Invoke-StaleTables $vid

    # умовне форматування: колонкові правила моделі + правила з областю (L6: scope row / tableCode, textEq/textNe) - одним PUT на версію.
    # Правила з областю потребують lane cellstyle-l6-3: сервер без неї міг би проігнорувати поля області й зробити правило колонковим
    # (фарбувало б усю колонку), тому після PUT читаємо набір назад і, якщо області не збереглися, відкочуємо до колонкових правил.
    $scopedRules = @(ConvertTo-LandCfRules $Mdl)
    if (@($Mdl.conditionalFormats).Count -gt 0 -or $scopedRules.Count -gt 0) {
        $g = Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$vid/conditional-formats"
        $etag = [string]$g.Headers['ETag']
        $rules = @($Mdl.conditionalFormats | ForEach-Object { @{ columnCode = [string]$_.columnCode; operator = [string]$_.operator; value = [string]$_.value; valueTo = $null
                    backgroundHex = [string]$_.backgroundHex; foregroundHex = [string]$_.foregroundHex; isBold = [bool]$_.isBold } })
        $plain = @($rules)
        $h = @{}; if ($etag) { $h['If-Match'] = $etag }
        $cr = Invoke-Ecr -Method PUT -Path "/api/v1/template-versions/$vid/conditional-formats" -Body @{ rules = @($plain + $scopedRules) } -Headers $h
        Log ("  умовне форматування: правил {0} (з областю {1}): HTTP {2} {3}" -f ($plain.Count + $scopedRules.Count), $scopedRules.Count, $cr.Status, $(if ($cr.Status -ne 200) { ([string]$cr.Text).Substring(0, [Math]::Min(250, ([string]$cr.Text).Length)) } else { '' }))
        if ($cr.Status -eq 200 -and $scopedRules.Count -gt 0) {
            $back = Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$vid/conditional-formats"
            $kept = @(@($back.Json) | Where-Object { $_.tableCode }).Count
            if ($kept -lt $scopedRules.Count) {
                Log "  ПОПЕРЕДЖЕННЯ: сервер зберіг $kept із $($scopedRules.Count) правил з областю (потрібна lane cellstyle-l6-3); відкочую до колонкових правил."
                $etag2 = [string]$back.Headers['ETag']
                $h2 = @{}; if ($etag2) { $h2['If-Match'] = $etag2 }
                $rb = Invoke-Ecr -Method PUT -Path "/api/v1/template-versions/$vid/conditional-formats" -Body @{ rules = $plain } -Headers $h2
                Log ("  відкат умовного форматування: HTTP {0}" -f $rb.Status)
            }
        }
    }
    # шапка Contract (13 полів) — офіційним скриптом на цій чернетці, з публікацією
    $pw = ConvertTo-SecureString $Acc['land-owner'] -AsPlainText -Force
    & (Join-Path $here 'Apply-ContractHeader.ps1') -TemplateCode $TemplateCode -BaseUrl $BaseUrl -User 'land-owner' -Password $pw -ConfirmStand $childStand -UseExistingDraft
    $vers = @((Api GET "/api/v1/templates/$($S.TemplateId)/versions?limit=100").Json.items)
    $pub = $vers | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1
    if ($null -eq $pub) { throw 'Після Apply-ContractHeader немає опублікованої версії.' }
    $S.VersionId = [int]$pub.id
    Log "  опубліковано версію $($pub.version) (id $($pub.id))"
    Set-TableHeaderStyles
}

# ==================== Project ====================
function Items($j) {
    if ($null -eq $j) { return @() }
    if ($j -is [array]) { return $j }
    if ($j.PSObject.Properties['items']) { return @($j.items) }
    return @($j)
}
function Set-RoleGrants([string]$roleCode, [object[]]$wanted) {
    $roles = Items (Api GET '/api/v1/roles').Json
    $role = $roles | Where-Object { $_.code -eq $roleCode } | Select-Object -First 1
    if ($null -eq $role) { throw "Немає ролі $roleCode" }
    $g = Api GET "/api/v1/roles/$($role.id)/grants"
    $etag = [string]$g.Headers['ETag']
    $cur = @()
    $src = $g.Json
    $list = if ($src.PSObject.Properties['grants']) { @($src.grants) } else { Items $src }
    foreach ($x in $list) {
        if ($null -ne $x.resourceId) { $cur += @{ resourceKind = [string]$x.resourceKind; resourceId = [int]$x.resourceId; level = [string]$x.level; isDeny = [bool]$x.isDeny } }
    }
    $merged = @($cur)
    foreach ($w in $wanted) {
        $same = @($merged | Where-Object { $_.resourceKind -eq $w.resourceKind -and $_.resourceId -eq $w.resourceId -and $_.isDeny -eq $w.isDeny })
        if ($same.Count -gt 0) { $same[0].level = $w.level; continue }
        $merged += $w
    }
    $h = @{}
    if ($etag) { $h['If-Match'] = $etag }
    $null = Api PUT "/api/v1/roles/$($role.id)/grants" @{ grants = $merged } $h
}

function Stage-Project {
    Log '== Project =='
    LoginAs 'land-owner'
    $pols = Items (Api GET '/api/v1/projects/period-policies').Json
    $pol = $pols | Where-Object { $_.code -eq 'LAND_OPEN' } | Select-Object -First 1
    if ($null -eq $pol) {
        $pol = (Api POST '/api/v1/projects/period-policies' @{ code = 'LAND_OPEN'; openOffsetDays = 0; graceOffsetDays = 800; hardCloseOffsetDays = 900; yearGraceOffsetDays = 900 }).Json
        Log '  політика LAND_OPEN створена'
    }
    $tpl = (Api GET ('/api/v1/templates?limit=50&q=' + $TemplateCode)).Json
    $t = @(Items $tpl) | Where-Object { $_.code -eq $TemplateCode } | Select-Object -First 1
    $pub = @(Items (Api GET "/api/v1/templates/$($t.id)/versions?limit=100").Json) | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1
    if ($null -eq $pub) { throw 'Немає опублікованої версії шаблону (етап Template).' }
    $S.VersionId = [int]$pub.id
    $projs = Items (Api GET '/api/v1/projects').Json
    $p = $projs | Where-Object { $_.code -eq $ProjectCode } | Select-Object -First 1
    if ($null -eq $p) {
        $c = Api POST '/api/v1/projects' @{ code = $ProjectCode; nameL10n = @{ en = "Land UI acceptance $Year" }; timeZoneId = 'Asia/Atyrau'; periodKind = 'Monthly'; year = $Year
            templateVersionId = $S.VersionId; periodPolicyId = [int]$pol.id; customPeriodCount = $null }
        $projs = Items (Api GET '/api/v1/projects').Json
        $p = $projs | Where-Object { $_.code -eq $ProjectCode } | Select-Object -First 1
        Log "  проєкт $ProjectCode створено (id $($p.id))"
    }
    $S.ProjectId = [int]$p.id
    # гранти ролей (Security.ManageRoles — bootstrap)
    LoginAs 'bootstrap'
    $pid_ = $S.ProjectId
    Set-RoleGrants 'LandOwner' @(@{ resourceKind = 'Project'; resourceId = $pid_; level = 'Manage'; isDeny = $false })
    Set-RoleGrants 'LandDataEntry' @(@{ resourceKind = 'Project'; resourceId = $pid_; level = 'Submit'; isDeny = $false })
    Set-RoleGrants 'LandApprover' @(@{ resourceKind = 'Project'; resourceId = $pid_; level = 'Manage'; isDeny = $false })
    Set-RoleGrants 'Viewer' @(@{ resourceKind = 'Project'; resourceId = $pid_; level = 'Read'; isDeny = $false })
    Set-RoleGrants 'LandRestricted' @(@{ resourceKind = 'Project'; resourceId = $pid_; level = 'Write'; isDeny = $false })
    LoginAs 'land-owner'
    # маршрут погодження = роль LandApprover
    $roles = Items (Api GET '/api/v1/roles').Json
    $ar = $roles | Where-Object { $_.code -eq 'LandApprover' } | Select-Object -First 1
    $rr = Invoke-Ecr -Method PUT -Path "/api/v1/projects/$pid_/approval-route" -Body @{ roleIds = @([int]$ar.id) }
    Log "  маршрут погодження: HTTP $($rr.Status)"
    $ac = Invoke-Ecr -Method POST -Path "/api/v1/projects/$pid_/activate"
    Log "  активація: HTTP $($ac.Status) $(Get-EcrErrorCode $ac)"
    $cal = (Api GET "/api/v1/projects/$pid_/periods").Json
    Log ('  періоди: ' + ((@($cal.periods) | ForEach-Object { "$($_.periodKey)=$($_.state)" }) -join ' '))
    # закріпити поточним червень (дані наповнення за шестим місяцем)
    $pp = @($cal.periods) | Where-Object { [int]$_.periodKey -eq ($Year * 100 + 6) } | Select-Object -First 1
    if ($pp) { $null = Invoke-Ecr -Method PUT -Path "/api/v1/projects/$pid_/current-period" -Body @{ pinnedPeriodId = [int]$pp.id; reason = 'Land UI acceptance base' } }
    # сценарій «deny»: роль LandRestricted не бачить аркуш «7. Water Report»
    Load-Structure
    if ($S.SheetIds.ContainsKey('S07_WATER_REPORT')) {
        LoginAs 'bootstrap'
        Set-RoleGrants 'LandRestricted' @(@{ resourceKind = 'Sheet'; resourceId = [int]$S.SheetIds['S07_WATER_REPORT']; level = 'Read'; isDeny = $true })
        LoginAs 'land-owner'
    }
}

# ==================== Structure / Documents ====================
function Load-Structure {
    $t = (Api GET ('/api/v1/templates?limit=50&q=' + $TemplateCode)).Json
    $tpl = @(Items $t) | Where-Object { $_.code -eq $TemplateCode } | Select-Object -First 1
    $pub = @(Items (Api GET "/api/v1/templates/$($tpl.id)/versions?limit=100").Json) | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1
    $S.VersionId = [int]$pub.id
    $st = (Api GET "/api/v1/template-versions/$($S.VersionId)/structure").Json
    $S.SheetIds = @{}; $S.Tables = @{}; $S.Cols = @{}; $S.ShortTable = @{}
    foreach ($sh in @($st.sheets)) {
        $S.SheetIds[[string]$sh.code] = [int]$sh.id
        foreach ($tb in @($sh.tables)) {
            $key = "$($sh.code)/$($tb.code)"
            $S.Tables[$key] = [int]$tb.id
            $S.ShortTable[[string]$tb.code] = $key
            $S.Cols[$key] = @{}
            foreach ($c in @($tb.columns)) { $S.Cols[$key][[string]$c.code] = [int]$c.id }
        }
    }
}
function ColId([string]$shortTable, [string]$col) {
    $k = $S.ShortTable[$shortTable]
    if (-not $k) { throw "Немає таблиці $shortTable" }
    $id = $S.Cols[$k][$col]
    if (-not $id) { throw "Немає колонки $shortTable/$col" }
    return [int]$id
}

# ==================== Methodologies ====================
$AF_TABLE = @{ ECW_C05_01 = 'T3_00'; ECW_C05_02 = 'T3_00'; ECW_C05_03 = 'T3_00'; ECW_C05_04 = 'T3_00'; ECW_C05_05 = 'T3_00'; ECW_C06_01 = 'T4_01'
    ECW_C07_07 = 'T5_08'; ECW_C07_08 = 'T5_09'; ECW_C07_21 = 'T5_27' }
$PublishList = @('ECW_C05_01', 'ECW_C05_02', 'ECW_C05_03', 'ECW_C05_04', 'ECW_C05_05', 'ECW_C06_01', 'ECW_C07_07', 'ECW_C07_08', 'ECW_C07_21')

function Stage-Methodologies {
    Log '== Methodologies =='
    LoginAs 'land-owner'
    Load-Structure
    Load-Registries
    $pr = Items (Api GET '/api/v1/projects').Json | Where-Object { $_.code -eq $ProjectCode } | Select-Object -First 1
    $S.ProjectId = [int]$pr.id
    $S.Units = @{}
    foreach ($u in @((Api GET '/api/v1/units').Json)) { $S.Units[[string]$u.code] = [int]$u.id }
    # 1. імпорт пакета AF (чернетки)
    $ms = Items (Api GET '/api/v1/methodologies').Json
    if (-not ($ms | Where-Object { $_.code -eq 'ECW_C05_01' })) {
        $pkg = Get-Content -LiteralPath $afFile -Raw -Encoding UTF8 | ConvertFrom-Json
        $dry = Api POST '/api/v1/methodologies/import?dryRun=true&timeZone=Asia%2FAtyrau' $pkg
        Log ("  dry-run імпорту: outcome={0}" -f $dry.Json.outcome)
        $imp = Api POST '/api/v1/methodologies/import?dryRun=false&timeZone=Asia%2FAtyrau' $pkg
        Log ("  імпорт: outcome={0}" -f $imp.Json.outcome)
        $ms = Items (Api GET '/api/v1/methodologies').Json
    }
    $mid = @{}
    foreach ($m in $ms) { $mid[[string]$m.code] = [int]$m.id }
    $running = RegEntryId 'LAND_GENERIC_EQUIPSTATUS' 'Running'
    function Rule([string]$tbl, [hashtable]$conds) {
        $h = [ordered]@{}
        foreach ($k in $conds.Keys) { $h[[string](ColId $tbl $k)] = [string]$conds[$k] }
        $h[[string](ColId $tbl 'Land_Status')] = [string]$running
        return ($h | ConvertTo-Json -Compress)
    }
    $eqReg = 'LAND_STATIONARY_TYPE_OF_EQUIPMENT'; $fuReg = 'LAND_STATIONARY_TYPE_OF_FUEL'
    function E3([string]$eq, [string]$fu) { return Rule 'T3_00' @{ Land_TypeEquipment = (RegEntryId $eqReg $eq); Land_TypeFuel = (RegEntryId $fuReg $fu) } }
    function Pairs($a, $b) { $o = @(); foreach ($x in $a) { foreach ($y in $b) { $o += , @($x, $y) } }; return $o }
    $rules = @{}
    $rules['ECW_C05_01'] = @(Pairs @('Compressor', 'Pump', 'Generator') @('Diesel', 'Kerosene') | ForEach-Object { E3 $_[0] $_[1] })
    $rules['ECW_C05_02'] = @(Pairs @('Compressor', 'Generator') @('Gasoline') | ForEach-Object { E3 $_[0] $_[1] })
    $rules['ECW_C05_03'] = @(E3 'Gasoline blow torch' 'Gasoline')
    $rules['ECW_C05_04'] = @(Pairs @('Heater-Blower', 'Hot-water boiler', 'Asphalt heater') @('Diesel', 'Kerosene') | ForEach-Object { E3 $_[0] $_[1] })
    $rules['ECW_C05_05'] = @(Pairs @('Steam boiler', 'Hot-water boiler') @('Makat Gas', 'Fuel gas (HMB)', 'LPG (HMB)', 'Fuel gas (Export)', 'Fuel gas (Internal)') | ForEach-Object { E3 $_[0] $_[1] })
    $rules['ECW_C06_01'] = @('Diesel', 'Gasoline' | ForEach-Object { Rule 'T4_01' @{ Land_TypeFuel = (RegEntryId 'LAND_GENERIC_TYPE_OF_FUEL' $_) } })
    $rules['ECW_C07_07'] = @('Bilge water', 'Diesel', 'Gasoline', 'Jet fuel', 'Lubricant', 'Recovered oil', 'Solvent' | ForEach-Object { Rule 'T5_08' @{ Land_TypeStorMaterial = (RegEntryId 'LAND_OTHER_TANKFARM' $_) } })
    $desc = RegEntryId 'LAND_OTHER_PETROLSTATION_DESCRIPTION_OF_EQUIPMENT' 'Petrol station'
    $rules['ECW_C07_08'] = @(Pairs @('Ground level tank', 'Undergound tank') @('Diesel', 'Gasoline', 'Jet fuel', 'Lubricant') | ForEach-Object {
            Rule 'T5_09' @{ Land_DescriptionEquipment = $desc; Land_TypePetrStationRef = (RegEntryId 'LAND_OTHER_PETROLSTATION_TYPE' $_[0]); Land_TypePumpedLiquid = (RegEntryId 'LAND_OTHER_PETROLSTATION' $_[1]) } })
    $rules['ECW_C07_21'] = @('LPG', 'Diesel', 'Gasoline', 'Propane' | ForEach-Object { Rule 'T5_27' @{ Land_TypeBurnFuelRef = (RegEntryId 'LAND_OTHER_STATIONARY_TYPEOFSTREAMS' $_) } })

    function NameOf([string]$reg, [string]$pre) {
        $r = @($Mdl.registries) | Where-Object { $_.code -eq $reg } | Select-Object -First 1
        foreach ($e in @($r.entries)) { if ([string]$e.en -like "$pre*" -or [string]$e.name -like "$pre*") { return [string]$e.name } }
        throw "Немає значення '$pre' у довіднику $reg"
    }
    $fd = NameOf 'LAND_STATIONARY_TYPE_OF_FUEL' 'Diesel'; $fk = NameOf 'LAND_STATIONARY_TYPE_OF_FUEL' 'Kerosene'
    $mfd = NameOf 'LAND_GENERIC_TYPE_OF_FUEL' 'Diesel'; $mfg = NameOf 'LAND_GENERIC_TYPE_OF_FUEL' 'Gasoline'
    $sm = @{}; foreach ($n in 'Diesel', 'Gasoline', 'Bilge water', 'Lubricant', 'Recovered oil') { $sm[$n] = NameOf 'LAND_OTHER_TANKFARM' $n }
    $pl = @{}; foreach ($n in 'Diesel', 'Gasoline', 'Jet fuel', 'Lubricant') { $pl[$n] = NameOf 'LAND_OTHER_PETROLSTATION' $n }
    $CAT = @{
        ECW_C05_02 = '!ECW_Category'
        ECW_C05_04 = "if(REGFIELD(@Land_TypeFuel,'NAME') = '$fd', 'Diesel', if(REGFIELD(@Land_TypeFuel,'NAME') = '$fk', 'Kerosene', ''))"
        ECW_C06_01 = "if(REGFIELD(@Land_TypeFuel,'NAME') = '$mfd', 'Diesel', if(REGFIELD(@Land_TypeFuel,'NAME') = '$mfg', 'Gasoline', ''))"
        ECW_C07_07 = "if(REGFIELD(@Land_TypeStorMaterial,'NAME') = '$($sm.Diesel)', 'Diesel', if(REGFIELD(@Land_TypeStorMaterial,'NAME') = '$($sm.Gasoline)', 'Gasoline', if(REGFIELD(@Land_TypeStorMaterial,'NAME') = '$($sm['Bilge water'])' or REGFIELD(@Land_TypeStorMaterial,'NAME') = '$($sm.Lubricant)', 'Base', if(REGFIELD(@Land_TypeStorMaterial,'NAME') = '$($sm['Recovered oil'])', 'Recovered', ''))))"
        ECW_C07_08 = "if(REGFIELD(@Land_TypePumpedLiquid,'NAME') = '$($pl.Diesel)', 'Diesel', if(REGFIELD(@Land_TypePumpedLiquid,'NAME') = '$($pl.Gasoline)', 'Gasoline', if(REGFIELD(@Land_TypePumpedLiquid,'NAME') = '$($pl['Jet fuel'])', 'Kerosene', if(REGFIELD(@Land_TypePumpedLiquid,'NAME') = '$($pl.Lubricant)', 'MineralOil', ''))))"
        ECW_C07_21 = '!ECW_Category'
    }
    $docId = [int]((Items (Api GET "/api/v1/documents?projectId=$($S.ProjectId)&limit=5").Json | Select-Object -First 1).id)
    if (-not $docId) { throw 'Немає документа для golden-тестів (спершу -Stage Documents).' }
    function Arg([string]$c, $v) {
        if ($v -is [hashtable]) { return @{ argumentCode = $c; value = $null; valueString = $null; unitId = $null; entryId = [int]$v.entryId } }
        if ($v -is [string]) { return @{ argumentCode = $c; value = $null; valueString = $v; unitId = $null } }
        return @{ argumentCode = $c; value = [double]$v; valueString = $null; unitId = $null }
    }
    $ovh = 'Not passed overhaul - Не прошел капитальный ремонт'
    $Tests = @{
        ECW_C05_01 = @{ case = 'P1'; tol = '0.000001'; inp = @{ Land_Power = 500; Land_Rotation = '<1500'; Land_OverhauledEngines = $ovh; Land_RunningHoursMonthly = 100; Land_Measure_FuelSubstanceConsumptionOrPumpedVolume = 20 }
            exp = @{ ECW_EC_tons_NOx = 0.8; ECW_EC_tons_301 = 0.64; ECW_EC_tons_304 = 0.104; ECW_EC_tons_337 = 0.52; ECW_EC_gsec_NOx = 1.333333; ECW_EC_gsec_301 = 1.066667 } }
        ECW_C05_02 = @{ case = 'P2'; tol = '0.000000001'; inp = @{ CalculationMonth = 6; Land_RunningHoursMonthly = 50; Land_Measure_FuelSubstanceConsumptionOrPumpedVolume = 0 }
            exp = @{ ECW_EC_tons_NOx = 0.00000875; ECW_EC_tons_301 = 0.000007; ECW_EC_tons_304 = 0.0000011375; ECW_EC_gsec_NOx = 0.000048611; ECW_EC_tons_337 = 0.00046875 } }
        ECW_C05_03 = @{ case = 'P3'; tol = '0.00000001'; inp = @{ Land_RunningHoursMonthly = 10; Land_Measure_FuelSubstanceConsumptionOrPumpedVolume = 0.01 }
            exp = @{ ECW_EC_tons_NOx = 0.000006; ECW_EC_tons_301 = 0.0000048; ECW_EC_gsec_NOx = 0.00016667 } }
        ECW_C05_04 = @{ case = 'ST025'; tol = '0.000001'; inp = @{ Land_Measure_FuelSubstanceConsumptionOrPumpedVolume = 4.684; Land_RunningHoursMonthly = 52; Land_EF_KNO2 = 0.113767; Land_TypeFuel = @{ reg = 'LAND_STATIONARY_TYPE_OF_FUEL'; name = $fd } }
            exp = @{ ECW_EC_tons_301 = 0.0182247; ECW_EC_gsec_301 = 0.0973543; ECW_EC_tons_337 = 0.0640771 } }
        ECW_C05_05 = @{ case = 'P4'; tol = '0.000001'; inp = @{ Density = 0.75; FuelLowerHeating = 36; H2Smass = 2; MercaptanSulphurContent = 0.5; Land_EF_KNO2 = 0.1; Land_GasConsumption = 100000; Land_RunningHoursMonthly = 100 }
            exp = @{ ECW_EC_tons_301 = 0.288; ECW_EC_tons_337 = 0.9; ECW_EC_tons_330 = 3.57 } }
        ECW_C06_01 = @{ case = 'P5'; tol = '0.000001'; inp = @{ Land_Measure_FuelConsumptionTonn = 2.5; Land_Measure_FuelConsumptionKgh = 40; Land_Measure_TotQuantTransport = 1; Land_TypeFuel = @{ reg = 'LAND_GENERIC_TYPE_OF_FUEL'; name = $mfd } }
            exp = @{ ECW_EC_tons_301 = 0.025; ECW_EC_tons_330 = 0.05; ECW_EC_tons_337 = 0.25; ECW_EC_gsec_301 = 0.111111; ECW_EC_gsec_337 = 1.111111 } }
        ECW_C07_07 = @{ case = 'D5'; tol = '0.00000001'; inp = @{ DaysInCalcMonth = 24; DaysInCalcYear = 365; Land_ERG_ExpFactor = 0.8; Land_ERG_MaximumVolume = 5; Land_ERG_StorEmissions = 0.081; Land_Measure_OperTimeDays = 24; Land_Measure_QuantLiquid = 0; Winter = 0
                Land_TypeStorMaterial = @{ reg = 'LAND_OTHER_TANKFARM'; name = $sm.Diesel } }
            exp = @{ ECW_EC_tons_333 = 0.000000043247; ECW_EC_gsec_333 = 0.0000121956; ECW_EC_gsec_2754 = 0.00434336 } }
        ECW_C07_21 = @{ case = 'IGN'; tol = '0.0000001'; inp = @{ Con_Region = 'Mangistau - Мангистау'; Land_TypeBurnFuel = 'Diesel - Дизель'; Land_Measure_IgnitionsNumber = 10 }
            exp = @{ ECW_EC_tons_301 = 0.000139; ECW_EC_tons_337 = 0.02175; ECW_EC_tons_328 = 0.00261 } }
        ECW_C07_08 = @{ case = 'OT021'; tol = '0.000000001'; inp = @{ Land_Measure_QuantFuel = 897.045; Land_ERG_Vsl = 45; Land_ERG_DurationAVGdischarge = 3600; Winter = 0
                Land_TypePetrStation = 'Ground level tank - Наземный резервуар'; Land_TypePumpedLiquid = @{ reg = 'LAND_OTHER_PETROLSTATION'; name = $pl.Diesel } }
            exp = @{ ECW_EC_tons_333 = 0.0001566508652414; ECW_EC_gsec_333 = 0.00007875; ECW_EC_tons_2754 = 0.0557900867209655; ECW_EC_gsec_2754 = 0.02804625 } }
    }
    foreach ($name in $PublishList) {
        $m = $mid[$name]
        if (-not $m) { Log "  ! методології $name немає"; continue }
        $tbl = $AF_TABLE[$name]
        $vers = Items (Api GET "/api/v1/methodologies/$m/versions").Json
        $v = $vers | Sort-Object id -Descending | Select-Object -First 1
        if ([string]$v.status -eq 'Published') { Log "  $name вже опублікована"; continue }
        $vid = [int]$v.id
        $fx = Items (Api GET "/api/v1/methodologies/$m/versions/$vid/formulas").Json
        $codes = @($fx | ForEach-Object { [string]$_.code } | Where-Object { $_ -match '^ECW_EC_' } | Sort-Object -Unique)
        $o = 1
        foreach ($c in $codes) {
            $u = if ($c -match '_tons_') { $S.Units['t'] } else { $S.Units['g_per_s'] }
            $null = Api PUT "/api/v1/methodologies/$m/versions/$vid/outputs/$c" @{ ordinal = $o++; unitId = $u }
        }
        foreach ($f in @($fx | Where-Object { $_.code -notmatch '^ECW_EC_' -and $_.code -match '^ECW_(Category|RepairStatus|Location)' })) {
            $r = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/formulas/$($f.code)" -Body @{ argumentsCsv = $f.argumentsCsv; expression = $f.expression; outputUnitId = $null; resultType = 'Text' }
            if ($r.Status -ne 200) { Log "  ! формула $($f.code): HTTP $($r.Status)" }
        }
        $k = 0
        foreach ($mj in @($rules[$name])) {
            $k++
            $r = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/rules/R_$($name -replace '^ECW_','')_$('{0:00}' -f $k)" -Body @{ isActive = $true; matchJson = $mj; priority = $k * 10 }
            if ($r.Status -ne 200) { Log "  ! рул ${name}: HTTP $($r.Status) $(Get-EcrErrorCode $r)" }
        }
        $bn = 0
        foreach ($c in $codes) {
            $cid = $S.Cols[$S.ShortTable[$tbl]][$c]
            if (-not $cid) { Log "  ! немає колонки $tbl/$c"; continue }
            $r = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/bindings/$cid/$c" -Body @{ isActive = $true; matchJson = '{}' }
            if ($r.Status -eq 200) { $bn++ } else { Log "  ! прив'язка ${name}/${c}: HTTP $($r.Status) $(Get-EcrErrorCode $r)" }
        }
        if ($CAT.ContainsKey($name)) {
            $r = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/category-rule" -Body @{ expression = $CAT[$name] }
            if ($r.Status -ne 200) { Log "  ! category-rule ${name}: HTTP $($r.Status) $(Get-EcrErrorCode $r)" }
        }
        $te = $Tests[$name]
        $pubStatus = 'no test'
        if ($te) {
            $args_ = @()
            foreach ($a in $te.inp.Keys) {
                $val = $te.inp[$a]
                if ($val -is [hashtable] -and $val.reg) { $val = @{ entryId = (RegEntryId $val.reg $val.name) } }
                $args_ += (Arg $a $val)
            }
            $inputJson = @{ documentId = $docId; periodKey = @{ value = ($Year * 100 + 6) }; arguments = $args_ } | ConvertTo-Json -Depth 8 -Compress
            $expJson = $te.exp | ConvertTo-Json -Compress
            $tr = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/tests/GOLD_$($te.case)" -Body @{ inputJson = $inputJson; expectedJson = $expJson; tolerance = $te.tol }
            $pubStatus = "test HTTP $($tr.Status) $(Get-EcrErrorCode $tr)"
        }
        LoginAs 'land-reviewer'
        $p = Invoke-Ecr -Method POST -Path "/api/v1/methodologies/$m/versions/$vid/publish" -Body @{ changeReason = 'Land UI base: AF import (original) + golden set'; effectiveFrom = '2024-01-01' }
        $tx = [string]$p.Text
        $res = 'OK'
        if ($p.Status -ne 200 -and $p.Status -ne 204) { $res = $tx.Substring(0, [Math]::Min(1200, $tx.Length)) }
        Log ("  {0}: рулів {1}, прив'язок {2}/{3}, {4}, публікація HTTP {5} {6}" -f $name, $k, $bn, $codes.Count, $pubStatus, $p.Status, $res)
        LoginAs 'land-owner'
        if ($p.Status -ne 200 -and $p.Status -ne 204) {
            # неопублікована методологія з активною прив'язкою валить усі перерахунки документа: вимикаємо прив'язки
            foreach ($c in $codes) {
                $cid = $S.Cols[$S.ShortTable[$tbl]][$c]
                if ($cid) { $null = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/bindings/$cid/$c" -Body @{ isActive = $false; matchJson = '{}' } }
            }
            Log "    прив'язки $name вимкнено (методологія лишилась чернеткою)"
        }
    }
    Bind-ResultColumns $mid
}

# LP-08: прив'язка виходів методологій C08/C09/C11/C12 (імпортує окремий процес: tools/Ecr.MethodologyImport + POST /methodologies/import)
# до колонок результатів ECW_EC_* таблиць аркушів 6, 9-11 (колонки створює Structure зі land-template.model.json, tb.calc).
# Методології, яких на сервері немає, пропускаються; виходу, якого немає у версії методології, ні. Прив'язка активна лише
# якщо остання версія опублікована: активна прив'язка до чернетки валить перерахунки документа (див. цикл вище).
function Bind-ResultColumns($mid) {
    $bound = 0; $off = 0; $noMeth = @{}; $noOut = 0; $noCol = 0
    $vinfo = @{}
    foreach ($sh in @($Mdl.sheets)) {
        if ($sh.calc) { continue }   # аркуші 3-5 ведуть цикл Stage-Methodologies вище
        foreach ($tb in @($sh.tables)) {
            if (-not $tb.calc) { continue }
            foreach ($name in @($tb.calc.methodologies)) {
                $m = $mid[[string]$name]
                if (-not $m) { $noMeth[[string]$name] = $true; continue }
                if (-not $vinfo.ContainsKey($name)) {
                    $vers = Items (Api GET "/api/v1/methodologies/$m/versions").Json
                    $v = $vers | Sort-Object id -Descending | Select-Object -First 1
                    $codes = @{}
                    foreach ($f in @(Items (Api GET "/api/v1/methodologies/$m/versions/$([int]$v.id)/formulas").Json)) { $codes[[string]$f.code] = $true }
                    $vinfo[$name] = @{ active = ([string]$v.status -eq 'Published'); codes = $codes }
                }
                $vi = $vinfo[$name]
                foreach ($o in @($tb.calc.outputs)) {
                    if (-not $vi.codes.ContainsKey([string]$o)) { $noOut++; continue }   # вихід належить іншій методології таблиці
                    $cid = $S.Cols[$S.ShortTable[[string]$tb.code]][[string]$o]
                    if (-not $cid) { $noCol++; Log "  ! немає колонки $($tb.code)/$o"; continue }
                    $r = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/bindings/$cid/$o" -Body @{ isActive = [bool]$vi.active; matchJson = '{}' }
                    if ($r.Status -eq 200) { if ($vi.active) { $bound++ } else { $off++ } }
                    else { Log "  ! прив'язка $name/$($tb.code)/${o}: HTTP $($r.Status) $(Get-EcrErrorCode $r)" }
                }
            }
        }
    }
    Log ("  колонки результатів (LP-08): прив'язок {0} активних, {1} вимкнених (чернетки); методологій на сервері немає: {2}; виходів без формули: {3}; без колонки: {4}" -f $bound, $off, $noMeth.Count, $noOut, $noCol)
}

# ==================== LandParity (LP-07: правила категорії C11/C12, склад речовин, аргументи тест-кейсу) ====================
# Лише явно: -Stage LandParity (в 'All' не входить). Для чернеток методологій, що вже є на стенді (імпорт AF-пакета
# tools/Ecr.MethodologyImport) і мають правило у docs/.../af-land-category-rules.json:
#   1. PUT .../category-rule  (вираз із файлу);
#   2. склад речовин версії = суфікси `_(\d{3,4})$` категорій її констант, зіставлені із записами LAND_AIR_SUBSTANCE
#      (код запису E<код>; довідник заводить Import-LandAirSubstances.ps1);
#   3. тест-кейс PARITY_1: додаються аргументи, які читає правило (@Land_NAE, @Land_TypeFuelOrStream, @Land_Streams,
#      @Land_InvNumEqp ...); наявні аргументи, очікуване й допуск не змінюються. Кандидати NAE пробуються по черзі
#      до першого зеленого simulate.
function Stage-LandParity {
    Log '== LandParity =='
    . (Join-Path $here 'LandParityRuleArgs.Lib.ps1')
    . (Join-Path $here 'LandSubstanceApplicability.Lib.ps1')
    LoginAs 'land-owner'
    $rulesFile = Join-Path $repo 'docs\delivery\reference-data\land-template\af-land-category-rules.json'
    $rules = @((Get-Content -LiteralPath $rulesFile -Raw -Encoding UTF8 | ConvertFrom-Json).rules)
    $ms = Items (Api GET '/api/v1/methodologies').Json
    $mid = @{}
    foreach ($m in $ms) { $mid[[string]$m.code] = [int]$m.id }

    $air = @{}
    $airReg = 'LAND_AIR_SUBSTANCE'
    $today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    $airResp = Invoke-Ecr -Method GET -Path "/api/v1/registries/$airReg/entries?asOf=$today"
    if ($airResp.Status -eq 200) {
        foreach ($e in @($airResp.Json)) { $air[[string]$e.code] = [int64]$e.id }
    }
    else { Log "  ! довідника $airReg немає (спершу Import-LandAirSubstances.ps1): склад речовин не задається" }

    $done = 0
    foreach ($r in $rules) {
        $name = [string]$r.methodology
        $expr = [string]$r.expression
        $m = $mid[$name]
        if (-not $m) { continue }
        $vers = Items (Api GET "/api/v1/methodologies/$m/versions").Json
        $v = $vers | Sort-Object id -Descending | Select-Object -First 1
        if ([string]$v.status -eq 'Published') { Log "  $name вже опублікована"; continue }
        $vid = [int]$v.id

        # 1. правило категорії
        $g = Invoke-Ecr -Method GET -Path "/api/v1/methodologies/$m/versions/$vid/category-rule"
        # ✎ 2026-10-13: обгортка застосовності (етап SubstanceApplicability) - те саме правило, не відхилення від файлу.
        if ($g.Status -eq 200 -and (Test-LandRuleMatchesBaseRule -Existing ([string]$g.Json.expression) -Base $expr)) {
            Log "  ${name}: правило категорії вже задане"
        }
        else {
            $p = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/category-rule" -Body @{ expression = $expr }
            if ($p.Status -ne 200) {
                Log "  ! ${name}: category-rule HTTP $($p.Status) $(Get-EcrErrorCode $p)"
                if (([string]$p.Text) -match 'categoryRuleUnknownArgument') {
                    # D1 (RC E): перевірка R16-4 звіряє @-аргументи з колонками ВСІХ прив'язок методології, і вимкнених теж.
                    Log "    ! прив'язки колонок результатів (LP-08) уже створено: -Stage Methodologies виконано після імпорту ${name}. Порядок: LandParity ДО повторного -Stage Methodologies (див. tools/land/README.md, «Порядок етапів»)."
                }
                continue
            }
            Log "  ${name}: правило категорії задано"
        }

        # 2. склад речовин з суфіксів категорій констант
        $consts = @((Api GET "/api/v1/methodologies/$m/versions/$vid/constants").Json)
        $cats = @($consts | ForEach-Object { [string]$_.category } | Where-Object { $_ } | Sort-Object -Unique)
        $codes = Get-LandSubstanceCodes -Categories $cats
        if ($codes.Count -gt 0 -and $air.Count -gt 0) {
            $ids = @(); $missing = @()
            foreach ($c in $codes) {
                if ($air.ContainsKey("E$c")) { $ids += $air["E$c"] } else { $missing += $c }
            }
            if ($missing.Count -gt 0) { Log "  ! ${name}: у довіднику $airReg немає кодів: $($missing -join ',')" }
            $cur = @((Api GET "/api/v1/methodologies/$m/versions/$vid/substances").Json.items | ForEach-Object { [int64]$_.substanceEntryId })
            $same = ($cur.Count -eq $ids.Count) -and (@($ids | Where-Object { $cur -notcontains $_ }).Count -eq 0)
            if ($ids.Count -gt 0 -and -not $same) {
                $null = Api PUT "/api/v1/methodologies/$m/versions/$vid/substances" @{ substanceEntryIds = @($ids) }
                Log "  ${name}: склад речовин = $($ids.Count) ($($codes -join ','))"
            }
        }

        # 3. аргументи тест-кейсу PARITY_1
        $tests = @(Items (Api GET "/api/v1/methodologies/$m/versions/$vid/tests").Json)
        $t = $tests | Where-Object { [string]$_.code -eq 'PARITY_1' } | Select-Object -First 1
        if (-not $t) { Log "  ${name}: тест-кейсу PARITY_1 немає — аргументи не додано"; continue }
        $inp = [string]$t.inputJson | ConvertFrom-Json
        $cands = Get-LandNaeCandidates -Categories $cats
        if ($cands.Count -eq 0) { $cands = @('') }
        $green = $false
        foreach ($cand in $cands) {
            $needed = Get-LandRuleSampleArguments -Expression $expr -NaeCandidate $cand
            $merged = Merge-LandParityArguments -Existing @($inp.arguments) -Needed $needed
            if ($merged.Added.Count -eq 0 -and $green) { break }
            $newInput = [ordered]@{ documentId = $inp.documentId; periodKey = $inp.periodKey; arguments = @($merged.Arguments) }
            $body = @{ inputJson = ($newInput | ConvertTo-Json -Depth 8 -Compress); expectedJson = [string]$t.expectedJson; tolerance = [string]$t.tolerance }
            $pt = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/tests/PARITY_1" -Body $body
            if ($pt.Status -ne 200) { Log "  ! ${name}: PARITY_1 HTTP $($pt.Status) $(Get-EcrErrorCode $pt)"; break }
            $sim = Invoke-Ecr -Method POST -Path "/api/v1/methodologies/$m/simulate" -Body @{ methodologyVersionId = $vid; periodKey = ($Year * 100 + 6) }
            if ($sim.Status -eq 200 -and $sim.Json.isGreen) { $green = $true; break }
        }
        Log ("  {0}: PARITY_1 аргументи {1}; simulate {2}" -f $name, (($merged.Added) -join ','), $(if ($green) { 'зелений' } else { 'НЕ зелений (див. категорію/константи)' }))
        $done++
    }
    Log "  оброблено методологій: $done"
}

# ==================== MassFraction (LP-10: Land_MassFraction за речовиною проходу) ====================
# Лише явно: -Stage MassFraction (або разом із -Stage CompositionMass). Для методологій з моделі
# (argumentBridges.massFraction.methodologies: ECW_C11_11_04) в ЧЕРНЕТЦІ токен @Land_MassFraction у виразах формул
# замінюється фрагментом if(CURRENT_SUBSTANCE() = '415', @Land_MassFraction_0415, ...), а аргументи формули
# доповнюються Land_MassFraction_0<код> (колонки-мости на таблиці 9a_19 дає модель). Фрагмент вживає
# CURRENT_SUBSTANCE()/REGFIND/REGFIELD - ярус Extension, у Legacy його не опублікувати (ECR-CALC-0433): чернетці з
# Legacy ставиться Strict (PUT .../modes; календар і трасування лишаються). Опубліковану версію не чіпаємо; нову
# версію чи публікацію етап не робить. Повторний запуск - 0 змін.
function Stage-MassFraction {
    Log '== MassFraction =='
    . (Join-Path $here 'LandHse400Composition.Lib.ps1')
    LoginAs 'land-owner'
    $mf = $Mdl.argumentBridges.massFraction
    if ($null -eq $mf) { throw 'У моделі немає argumentBridges.massFraction: перегенеруйте модель (model-gen).' }
    $today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    $reg = Invoke-Ecr -Method GET -Path "/api/v1/registries/LAND_HSE400_SUBSTANCE/entries?asOf=$today"
    if ($reg.Status -ne 200 -or @($reg.Json).Count -eq 0) {
        Log '  ! довідника LAND_HSE400_SUBSTANCE немає або він порожній (спершу -Stage Registries чи Import-LandHse400Substance.ps1): нічого не змінено'
        return
    }
    $ms = Items (Api GET '/api/v1/methodologies').Json
    $mid = @{}
    foreach ($m in $ms) { $mid[[string]$m.code] = [int]$m.id }
    $changed = 0
    foreach ($p in $mf.methodologies.PSObject.Properties) {
        $name = $p.Name
        $m = $mid[$name]
        if (-not $m) { Log "  ${name}: методології на сервері немає - пропущено"; continue }
        $vers = Items (Api GET "/api/v1/methodologies/$m/versions").Json
        $v = $vers | Sort-Object id -Descending | Select-Object -First 1
        if ([string]$v.status -eq 'Published') { Log "  ${name}: остання версія опублікована - не чіпаю"; continue }
        $vid = [int]$v.id
        $n = 0
        foreach ($f in @(Items (Api GET "/api/v1/methodologies/$m/versions/$vid/formulas").Json)) {
            $r = Convert-LandHse400CompositionMassFormula -Expression ([string]$f.expression) -ArgumentsCsv $f.argumentsCsv `
                -Fragment ([string]$p.Value.fragment) -StreamArguments @($p.Value.arguments) -ArgName ([string]$p.Value.token)
            if (-not $r.Changed) { continue }
            $body = @{ expression = $r.Expression; resultType = $f.resultType; outputUnitId = $f.outputUnitId; argumentsCsv = $r.ArgumentsCsv }
            if ($null -ne $f.scope) { $body.scope = $f.scope }
            $put = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/formulas/$($f.code)" -Body $body
            if ($put.Status -eq 200) { $n++ } else { Log "  ! ${name}/$($f.code): HTTP $($put.Status) $(Get-EcrErrorCode $put)" }
        }
        if ($n -gt 0 -and [string]$v.numericMode -ne 'Strict') {
            $md = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/modes" -Body @{
                numericMode = 'Strict'; calendarMode = [string]$v.calendarMode; traceLevel = [string]$v.traceLevel }
            if ($md.Status -eq 200) { Log "  ${name}: версія $vid -> Strict" } else { Log "  ! ${name}: modes HTTP $($md.Status) $(Get-EcrErrorCode $md)" }
        }
        $changed += $n
        Log "  ${name}: формул із фрагментом Land_MassFraction - $n"
    }
    Log "  змінено формул: $changed"
}

# ==================== CompositionMass (LP-10: склад газу HSE400 по речовині) ====================
# Лише явно: -Stage CompositionMass. Для методологій з моделі (argumentBridges.hse400.compositionMass.methodologies)
# в ЧЕРНЕТКАХ (опубліковану версію не чіпаємо) токен @CompositionMass у виразах формул замінюється фрагментом, а
# аргументи формули доповнюються аргументом потоку. Повторний запуск - 0 змін. Нову версію чи публікацію етап
# не робить: це окреме рішення власника методології.
function Stage-CompositionMass {
    Log '== CompositionMass =='
    . (Join-Path $here 'LandHse400Composition.Lib.ps1')
    LoginAs 'land-owner'
    $cm = $Mdl.argumentBridges.hse400.compositionMass
    if ($null -eq $cm) { throw 'У моделі немає argumentBridges.hse400.compositionMass: перегенеруйте модель (model-gen).' }
    $today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    $reg = Invoke-Ecr -Method GET -Path "/api/v1/registries/$($cm.substanceRegistry)/entries?asOf=$today"
    if ($reg.Status -ne 200 -or @($reg.Json).Count -eq 0) {
        Log "  ! довідника $($cm.substanceRegistry) немає або він порожній (спершу -Stage Registries чи Import-LandHse400Substance.ps1): нічого не змінено"
        return
    }
    $ms = Items (Api GET '/api/v1/methodologies').Json
    $mid = @{}
    foreach ($m in $ms) { $mid[[string]$m.code] = [int]$m.id }
    $changed = 0
    foreach ($p in $cm.methodologies.PSObject.Properties) {
        $name = $p.Name
        $m = $mid[$name]
        if (-not $m) { Log "  ${name}: методології на сервері немає - пропущено"; continue }
        $vers = Items (Api GET "/api/v1/methodologies/$m/versions").Json
        $v = $vers | Sort-Object id -Descending | Select-Object -First 1
        if ([string]$v.status -eq 'Published') { Log "  ${name}: остання версія опублікована - не чіпаю"; continue }
        $vid = [int]$v.id
        $n = 0
        $hasFragment = $false
        $fs = @(Items (Api GET "/api/v1/methodologies/$m/versions/$vid/formulas").Json)
        foreach ($f in $fs) {
            if ([string]$f.expression -match 'CURRENT_SUBSTANCE\s*\(') { $hasFragment = $true }
            $r = Convert-LandHse400CompositionMassFormula -Expression ([string]$f.expression) -ArgumentsCsv $f.argumentsCsv `
                -Fragment ([string]$p.Value.fragment) -StreamArguments @($p.Value.arguments)
            if (-not $r.Changed) { continue }
            $body = @{ expression = $r.Expression; resultType = $f.resultType; outputUnitId = $f.outputUnitId; argumentsCsv = $r.ArgumentsCsv }
            if ($null -ne $f.scope) { $body.scope = $f.scope }
            $put = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/formulas/$($f.code)" -Body $body
            if ($put.Status -eq 200) { $n++; $hasFragment = $true } else { Log "  ! ${name}/$($f.code): HTTP $($put.Status) $(Get-EcrErrorCode $put)" }
        }
        $changed += $n
        Log "  ${name}: формул із фрагментом складу - $n"

        # Фрагмент вживає CURRENT_SUBSTANCE() і REGFIND/REGFIELD - функції ярусу Extension: у версії з NumericMode=Legacy
        # публікація дає ECR-CALC-0433 (RC31: C11_01/02/10, C12_01). Тому чернетка з фрагментом переводиться в Strict
        # (PUT .../versions/{vid}/modes; календарний режим і журнал лишаються як є). Strict - явне рішення з нової дати
        # дії, не заднім числом: перша версія методології (як тут) переходу не має. ⚠ Strict міняє арифметику версії:
        # double(NCalc 1.3.8) -> decimal, ділення на нуль -> #DIV/0 замість +∞, округлення Round -> AwayFromZero.
        # -KeepNumericMode лишає режим як є (тоді публікація дасть 0433 для версій із фрагментом).
        if ($hasFragment -and -not $KeepNumericMode) {
            if ([string]$v.numericMode -eq 'Strict') {
                Log "  ${name}: версія $vid уже Strict"
            } else {
                $mb = @{ numericMode = 'Strict'; calendarMode = [string]$v.calendarMode; traceLevel = [string]$v.traceLevel }
                $mr = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/modes" -Body $mb
                if ($mr.Status -eq 200) { Log "  ${name}: версія $vid -> NumericMode=Strict (було $($v.numericMode))" }
                else { Log "  ! ${name}: режим Strict не задано: HTTP $($mr.Status) $(Get-EcrErrorCode $mr)" }
            }
        }
    }
    Log "  змінено формул: $changed"
}

# ==================== SubstanceApplicability (рішення людини 13.10: застосовність речовин на рядок) ====================
# Лише явно: -Stage SubstanceApplicability. Для методологій з моделі (substanceApplicability.methodologies) в ЧЕРНЕТКАХ
# наявне правило категорії обгортається SUBSTANCE_APPLICABLE(<довідник>, <методологія>, <вид>, <значення селектора>):
# речовина, якої немає в списку кодів рядка старого C#, для рядка пропускається (див. 02b). Правила, якого ще немає,
# етап не вигадує (спершу -Stage LandParity). Опубліковану версію не чіпаємо. Повтор - 0 змін.
function Stage-SubstanceApplicability {
    Log '== SubstanceApplicability =='
    . (Join-Path $here 'LandSubstanceApplicability.Lib.ps1')
    LoginAs 'land-owner'
    $sa = $Mdl.substanceApplicability
    if ($null -eq $sa) { throw 'У моделі немає substanceApplicability: перегенеруйте модель (model-gen).' }
    $today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    $reg = Invoke-Ecr -Method GET -Path "/api/v1/registries/$($sa.registry)/entries?asOf=$today"
    if ($reg.Status -ne 200 -or @($reg.Json).Count -eq 0) {
        Log "  ! довідника $($sa.registry) немає або він порожній (спершу -Stage Registries): нічого не змінено"
        return
    }
    $ms = Items (Api GET '/api/v1/methodologies').Json
    $mid = @{}
    foreach ($m in $ms) { $mid[[string]$m.code] = [int]$m.id }
    $changed = 0
    foreach ($p in $sa.methodologies.PSObject.Properties) {
        $name = $p.Name
        $m = $mid[$name]
        if (-not $m) { Log "  ${name}: методології на сервері немає - пропущено"; continue }
        $vers = Items (Api GET "/api/v1/methodologies/$m/versions").Json
        $v = $vers | Sort-Object id -Descending | Select-Object -First 1
        if ([string]$v.status -eq 'Published') { Log "  ${name}: остання версія опублікована - не чіпаю"; continue }
        $vid = [int]$v.id
        if ([string]$v.level -eq 'Legacy') { Log "  ! ${name}: версія $vid рівня Legacy - SUBSTANCE_APPLICABLE не опублікується (ECR-CALC-0433)" }
        $g = Invoke-Ecr -Method GET -Path "/api/v1/methodologies/$m/versions/$vid/category-rule"
        $current = if ($g.Status -eq 200) { [string]$g.Json.expression } else { '' }
        if ([string]::IsNullOrWhiteSpace($current)) { Log "  ${name}: правила категорії ще немає - спершу -Stage LandParity"; continue }
        if (Test-LandSubstanceApplicabilityRule -Rule $current) { Log "  ${name}: обгортка вже є"; continue }
        $wrapped = New-LandSubstanceApplicabilityRule -Inner $current -Registry ([string]$sa.registry) `
            -Methodology $name -Kind ([string]$p.Value.kind) -ValueExpression ([string]$p.Value.valueExpression)
        $put = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/category-rule" -Body @{ expression = $wrapped }
        if ($put.Status -eq 200) { $changed++; Log "  ${name}: правило категорії обгорнуто ($($p.Value.kind), записів $($p.Value.rows))" }
        else { Log "  ! ${name}: category-rule HTTP $($put.Status) $(Get-EcrErrorCode $put)" }
    }
    Log "  змінено правил: $changed"
}

# ==================== ParityBranch (C12_02: вибір type1/type2 за наявністю компонента) ====================
# Лише явно: -Stage ParityBranch. У ЧЕРНЕТЦІ методології з LandParityBranch.Lib.ps1 формули type2 компонентів
# обгортаються `if(@Land_Measure_Component_<код> = NULL, <вираз>, NULL)`: старий код C12_02 рахує для компонента лише
# type1, якщо значення є, інакше лише type2 (415 - завжди type2), а в ECR обидві гілки - виходи. Опубліковану версію не
# чіпаємо; нову версію чи публікацію етап не робить. Повторний запуск - 0 змін.
function Stage-ParityBranch {
    Log '== ParityBranch =='
    . (Join-Path $here 'LandParityBranch.Lib.ps1')
    LoginAs 'land-owner'
    $ms = Items (Api GET '/api/v1/methodologies').Json
    $mid = @{}
    foreach ($m in $ms) { $mid[[string]$m.code] = [int]$m.id }
    $changed = 0
    $plan = Get-LandParityBranchMethodologies
    foreach ($name in $plan.Keys) {
        $m = $mid[$name]
        if (-not $m) { Log "  ${name}: методології на сервері немає - пропущено"; continue }
        $vers = Items (Api GET "/api/v1/methodologies/$m/versions").Json
        $v = $vers | Sort-Object id -Descending | Select-Object -First 1
        if ([string]$v.status -eq 'Published') { Log "  ${name}: остання версія опублікована - не чіпаю"; continue }
        $vid = [int]$v.id
        $n = 0
        foreach ($f in @(Items (Api GET "/api/v1/methodologies/$m/versions/$vid/formulas").Json)) {
            $component = Get-LandParityBranchComponent -FormulaCode ([string]$f.code) -Components @($plan[$name])
            if ($null -eq $component) { continue }
            $r = Convert-LandParityBranchFormula -Expression ([string]$f.expression) -ArgumentsCsv $f.argumentsCsv -Component $component
            if (-not $r.Changed) { continue }
            $body = @{ expression = $r.Expression; resultType = $f.resultType; outputUnitId = $f.outputUnitId; argumentsCsv = $r.ArgumentsCsv }
            if ($null -ne $f.scope) { $body.scope = $f.scope }
            $put = Invoke-Ecr -Method PUT -Path "/api/v1/methodologies/$m/versions/$vid/formulas/$($f.code)" -Body $body
            if ($put.Status -eq 200) { $n++ } else { Log "  ! ${name}/$($f.code): HTTP $($put.Status) $(Get-EcrErrorCode $put)" }
        }
        $changed += $n
        Log "  ${name}: формул type2 обгорнуто - $n"
    }
    Log "  змінено формул: $changed"
}
function Stage-Documents {
    Log '== Documents =='
    LoginAs 'land-owner'
    Load-Structure
    $pr = Items (Api GET '/api/v1/projects').Json | Where-Object { $_.code -eq $ProjectCode } | Select-Object -First 1
    $S.ProjectId = [int]$pr.id
    $docs = Items (Api GET "/api/v1/documents?projectId=$($S.ProjectId)&limit=100").Json
    $byName = @{}
    foreach ($d in $docs) { $n = $d.nameL10n; $nm = if ($null -ne $n -and $n.PSObject.Properties['values']) { [string]$n.values.en } elseif ($null -ne $n) { [string]$n.en } else { [string]$d.businessKey }; $byName[$nm] = [int]$d.id }
    $sheetIds = @($S.SheetIds.Values | ForEach-Object { [int]$_ })
    $created = @{}
    foreach ($dn in 'LAND-GOLDEN', 'LAND-FULL') {
        if ($byName.ContainsKey($dn)) { continue }
        $d = Api POST '/api/v1/documents' @{ name = @{ en = $dn }; projectId = $S.ProjectId; sheetDefIds = $sheetIds; templateVersionId = $S.VersionId }
        $id = if ($d.Json.documentId) { $d.Json.documentId } else { $d.Json.id }
        $byName[$dn] = [int]$id; $created[$dn] = $true
        Log "  документ $dn створено (id $id)"
    }
}

function Stage-Data {
    Log '== Data (наповнення документів, стани, перерахунок) =='
    LoginAs 'land-owner'
    Load-Structure
    Load-Registries
    $pr = Items (Api GET '/api/v1/projects').Json | Where-Object { $_.code -eq $ProjectCode } | Select-Object -First 1
    $S.ProjectId = [int]$pr.id
    $byName = @{}
    foreach ($d in (Items (Api GET "/api/v1/documents?projectId=$($S.ProjectId)&limit=100").Json)) {
        $n = $d.nameL10n; $nm = if ($null -ne $n -and $n.PSObject.Properties['values']) { [string]$n.values.en } else { [string]$d.businessKey }
        $byName[$nm] = [int]$d.id
    }
    foreach ($dn in 'LAND-GOLDEN', 'LAND-FULL') { Set-DocumentHeader $byName[$dn] }
    # повернути на доопрацювання аркуші, що були подані/затверджені попереднім прогоном (повторний запуск)
    LoginAs 'land-approver'
    foreach ($sc in 'S09B_SULFUR', 'S10A_OFF_UTILITY_AI', 'S11B_OFF_PROCESS_EPC2') {
        $null = Invoke-Ecr -Method POST -Path "/api/v1/documents/$($byName['LAND-FULL'])/reopen" -Body @{ periodKey = ($Year * 100 + 6); sheetDefId = [int]$S.SheetIds[$sc]; reason = 'Land UI base: refill' }
    }
    LoginAs 'land-owner'
    Fill-GoldenDocument $byName['LAND-GOLDEN']
    Fill-FullDocument $byName['LAND-FULL']
    # перерахунок золотого документа по місяцях з даними
    # період-мажорний порядок (спершу всі документи періоду) + пейсинг: межа 6/хв на пару користувач+документ не досягається
    foreach ($p in 202603, 202604, 202605, 202606) {
        foreach ($dn in 'LAND-GOLDEN') {
            Wait-RecalcPace
            $r = Invoke-Ecr -Method POST -Path "/api/v1/documents/$($byName[$dn])/recalculate" -Body @{ periodKey = $p }
            Log ("  перерахунок ${dn} ${p}: HTTP $($r.Status)")
        }
    }
    # стани: аркуші LAND-FULL — Approved / Rejected / Submitted
    $full = $byName['LAND-FULL']
    $wf = @(@('S09B_SULFUR', 'approve'), @('S10A_OFF_UTILITY_AI', 'reject'), @('S11B_OFF_PROCESS_EPC2', 'submit'))
    foreach ($w in $wf) {
        $sid = [int]$S.SheetIds[$w[0]]
        LoginAs 'land-entry'
        $r = Invoke-Ecr -Method POST -Path "/api/v1/documents/$full/submit" -Body @{ periodKey = ($Year * 100 + 6); sheetDefId = $sid; acknowledgeWarnings = $true }
        Log ("  submit {0}: HTTP {1} {2}" -f $w[0], $r.Status, $(if ($r.Status -ge 400) { ([string]$r.Text).Substring(0, [Math]::Min(200, ([string]$r.Text).Length)) } else { '' }))
        if ($r.Status -lt 300 -and $w[1] -ne 'submit') {
            LoginAs 'land-approver'
            $body = @{ periodKey = ($Year * 100 + 6); sheetDefId = $sid; approved = ($w[1] -eq 'approve'); reason = $(if ($w[1] -eq 'reject') { 'Check values (acceptance scenario)' } else { $null }) }
            $a = Invoke-Ecr -Method POST -Path "/api/v1/documents/$full/approve" -Body $body
            Log ("  {0} {1}: HTTP {2}" -f $w[1], $w[0], $a.Status)
        }
    }
    LoginAs 'land-owner'
}

# ---------- наповнення документів ----------
function Set-DocumentHeader([int]$docId) {
    $h = (Api GET "/api/v1/documents/$docId/header").Json
    $today = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    $pick = @{ Area = @('LAND_AREA', 'Island'); Contractor = @('LAND_CONTRACTOR', 'AGS'); Region = @('LAND_REGION', 'Mangistau'); Location = @('LAND_LOCATION', 'LQB')
        OnOffshore = @('LAND_ONOFFSHORE', 'Onshore'); TypeOfActivity = @('LAND_ACTIVITY', 'Operation'); Permit = @('LAND_PERMIT', 'KZ06VCZ14825472') }
    $fields = @()
    foreach ($k in $pick.Keys) {
        $es = @((Api GET "/api/v1/registries/$($pick[$k][0])/entries?asOf=$today").Json)
        $e = $es | Where-Object { ([string]$_.display).Trim() -like "$($pick[$k][1])*" } | Select-Object -First 1
        if ($null -eq $e) { $e = $es | Select-Object -First 1 }
        if ($e) { $fields += @{ code = $k; value = [int]$e.id; isEmpty = $false } }
    }
    $fields += @{ code = 'FilledBy'; value = 'Land UI test'; isEmpty = $false }
    $fields += @{ code = 'ContractHolder'; value = 'Test holder'; isEmpty = $false }
    $fields += @{ code = 'ContractNumber'; value = '321'; isEmpty = $false }
    $fields += @{ code = 'ProcessedOn'; value = 'Version 2026.001 - 4.6.2026'; isEmpty = $false }
    $r = Invoke-Ecr -Method PATCH -Path "/api/v1/documents/$docId/header" -Body @{ fields = $fields; baseVersion = [string]$h.version }
    Log ("  шапка документа ${docId}: HTTP $($r.Status)")
}

function Cell([string]$col, $val) { return @{ columnCode = $col; isEmpty = $false; value = $val } }
function Month2([int]$m) { return ('{0:00}' -f $m) }
function TableInstances([int]$docId, [int]$period) {
    $r = (Api GET "/api/v1/documents/$docId/tables?periodKey=$period").Json
    $h = @{}
    foreach ($t in @($r)) { $k = "$($t.sheetCode)/$($t.tableCode)"; $h[$k] = [int]$t.tableInstanceId }
    return $h
}
function Patch-Rows([int]$docId, [int]$period, [int]$ti, $rows, [string]$what) {
    if (@($rows).Count -eq 0) { return 0 }
    # наявні рядки (повторний запуск): baseVersion = rowVersion, нові — null
    $g = (Api GET "/api/v1/documents/$docId/tables/${ti}?periodKey=$period").Json
    $rv = @{}
    foreach ($r in @($g.rows)) { $rv[[string]$r.rowKey] = [string]$r.rowVersion }
    $prepared = @()
    foreach ($row in @($rows)) {
        $seen = @{}
        $cells = @()
        foreach ($c in @($row.cells)) { $seen[[string]$c.columnCode] = $c }   # кожна комірка — один раз
        foreach ($k in $seen.Keys) { $cells += $seen[$k] }
        $bv = $null; if ($rv.ContainsKey([string]$row.rowKey)) { $bv = $rv[[string]$row.rowKey] }
        $prepared += , @{ rowKey = [string]$row.rowKey; baseVersion = $bv; cells = $cells }
    }
    $done = 0; $failed = 0; $lastErr = ''
    for ($i = 0; $i -lt $prepared.Count; $i += 40) {
        $chunk = @($prepared)[$i..([Math]::Min($i + 39, $prepared.Count - 1))]
        $r = Invoke-Ecr -Method PATCH -Path "/api/v1/documents/$docId/cells" -Body @{ origin = 'UserEdit'; periodKey = $period; tableInstanceId = $ti; rows = $chunk }
        if ($r.Status -eq 200) { $done += $chunk.Count; continue }
        foreach ($one in $chunk) {   # пачка відхилена: рятуємо валідні рядки по одному
            $r1 = Invoke-Ecr -Method PATCH -Path "/api/v1/documents/$docId/cells" -Body @{ origin = 'UserEdit'; periodKey = $period; tableInstanceId = $ti; rows = @($one) }
            if ($r1.Status -eq 200) { $done++ } else { $failed++; $lastErr = "HTTP $($r1.Status) $(Get-EcrErrorCode $r1)" }
        }
    }
    if ($failed -gt 0) { Log ("  ! {0}: відхилено рядків {1} ({2})" -f $what, $failed, $lastErr) }
    return $done
}
function Entry([string]$reg, [string]$name) {
    if ([string]::IsNullOrWhiteSpace($name) -or $name -eq 'NULL') { return $null }
    try { return [string](RegEntryId $reg $name) } catch { return $null }
}

# Золотий документ: приклади P1..P5 зі спеки + (якщо є -GoldenDir) рядки golden-*.csv (числа з вигрузки старої системи, без ПІБ).
function Fill-GoldenDocument([int]$docId) {
    Log "  наповнення LAND-GOLDEN (id $docId)"
    $running = Entry 'LAND_GENERIC_EQUIPSTATUS' 'Running'
    $ovh = @{ 'Not passed overhaul' = 'Not passed overhaul'; 'Passed overhaul' = 'Passed overhaul' }
    $mkStat = {
        param($rk, $mm, $eq, $fu, $power, $rot, $ovhName, $hours, $fuel, $gas, $kno2)
        $script:rowNo = 1 + [int]$script:rowNo
        $c = @((Cell 'Land_No' $script:rowNo), (Cell 'Land_Status' $running))
        $e = Entry 'LAND_STATIONARY_TYPE_OF_EQUIPMENT' $eq; if ($e) { $c += Cell 'Land_TypeEquipment' $e }
        $f = Entry 'LAND_STATIONARY_TYPE_OF_FUEL' $fu; if ($f) { $c += Cell 'Land_TypeFuel' $f }
        if ($null -ne $power) { $c += Cell 'Land_Power' $power }
        $ro = Entry 'LAND_STATIONARY_ROTATION_SPEED' $rot; if ($ro) { $c += Cell 'Land_RotationRef' $ro }
        $ov = Entry 'LAND_STATIONARY_OVERHAUL' $ovhName; if ($ov) { $c += Cell 'Land_OverhauledEnginesRef' $ov }
        if ($null -ne $hours) { $maxH = @(744, 672, 744, 720, 744, 720, 744, 744, 720, 744, 720, 744)[$mm - 1]; $c += Cell ('Land_RunningHoursMonthly_M' + (Month2 $mm)) ([Math]::Min([double]$hours, $maxH)) }
        if ($null -ne $fuel) { $c += Cell ('Land_Measure_FuelSubstanceConsumptionOrPumpedVolume_M' + (Month2 $mm)) $fuel }
        if ($null -ne $gas) { $c += Cell ('Land_GasConsumption_M' + (Month2 $mm)) $gas }
        if ($null -ne $kno2) { $c += Cell 'Land_EF_KNO2' $kno2 }
        return @{ rowKey = $rk; baseVersion = $null; cells = $c }
    }
    $y = $Year * 100
    $stat = @{}   # period -> rows
    function AddS($p, $row) { if (-not $stat.ContainsKey($p)) { $stat[$p] = @() }; $stat[$p] += , $row }
    AddS ($y + 6) (& $mkStat 'P1_GEN_DSL' 6 'Generator' 'Diesel' 500 '<1500' 'Not passed overhaul' 100 20 $null $null)
    AddS ($y + 6) (& $mkStat 'P2_CMP_GAS' 6 'Compressor' 'Gasoline' 30 '<1500' 'Not passed overhaul' 50 0.2 $null $null)
    AddS ($y + 6) (& $mkStat 'P3_TORCH' 6 'Gasoline blow torch' 'Gasoline' $null $null $null 10 0.01 $null $null)
    AddS ($y + 6) (& $mkStat 'P4_BOILER_HWB_DSL' 6 'Hot-water boiler' 'Diesel' $null $null $null 52 4.684 $null 0.113767)
    AddS ($y + 6) (& $mkStat 'P4_BOILER_GAS' 6 'Steam boiler' 'Makat Gas' $null $null $null 100 $null 100000 0.1)
    AddS ($y + 6) (& $mkStat 'P6_REMOVED' 6 'Compressor' 'Diesel' 100 '<1500' 'Passed overhaul' 0 0 $null $null)
    $mob = @{}; $oth = @{}
    function AddM($p, $row) { if (-not $mob.ContainsKey($p)) { $mob[$p] = @() }; $mob[$p] += , $row }
    function AddO($tbl, $p, $row) { $k = "$tbl|$p"; if (-not $oth.ContainsKey($k)) { $oth[$k] = @() }; $oth[$k] += , $row }
    $mm6 = Month2 6
    AddM ($y + 6) @{ rowKey = 'P5_MOB_DSL'; baseVersion = $null; cells = @((Cell 'Land_No' 1), (Cell 'Land_Status' $running), (Cell 'Land_TypeFuel' (Entry 'LAND_GENERIC_TYPE_OF_FUEL' 'Diesel')),
            (Cell "Land_Measure_TotQuantTransport_M$mm6" 1), (Cell "Land_Measure_FuelConsumptionKgh_M$mm6" 40), (Cell "Land_Measure_FuelConsumptionTonn_M$mm6" 2.5)) }
    AddM ($y + 6) @{ rowKey = 'P5_MOB_GAS'; baseVersion = $null; cells = @((Cell 'Land_No' 2), (Cell 'Land_Status' $running), (Cell 'Land_TypeFuel' (Entry 'LAND_GENERIC_TYPE_OF_FUEL' 'Gasoline')),
            (Cell "Land_Measure_TotQuantTransport_M$mm6" 2), (Cell "Land_Measure_FuelConsumptionKgh_M$mm6" 30), (Cell "Land_Measure_FuelConsumptionTonn_M$mm6" 2)) }
    AddO 'S05_OTHER_EQUIPMENT/T5_08' ($y + 5) @{ rowKey = 'D5_TANK_DSL'; baseVersion = $null; cells = @((Cell 'Land_No' 1), (Cell 'Land_Status' $running),
            (Cell 'Land_TypeStorMaterial' (Entry 'LAND_OTHER_TANKFARM' 'Diesel')), (Cell 'Land_ERG_ExpFactor' 0.8), (Cell 'Land_ERG_MaximumVolume' 5), (Cell 'Land_ERG_StorEmissions' 0.081),
            (Cell 'Land_Measure_QuantLiquid_M05' 0), (Cell 'Land_Measure_OperTimeDays_M05' 24)) }
    AddO 'S05_OTHER_EQUIPMENT/T5_09' ($y + 3) @{ rowKey = 'OT021_PETROL'; baseVersion = $null; cells = @((Cell 'Land_No' 1), (Cell 'Land_Status' $running),
            (Cell 'Land_DescriptionEquipment' (Entry 'LAND_OTHER_PETROLSTATION_DESCRIPTION_OF_EQUIPMENT' 'Petrol station')),
            (Cell 'Land_TypePetrStationRef' (Entry 'LAND_OTHER_PETROLSTATION_TYPE' 'Ground level tank')), (Cell 'Land_TypePumpedLiquid' (Entry 'LAND_OTHER_PETROLSTATION' 'Diesel')),
            (Cell 'Land_Measure_QuantFuel_M03' 897.045), (Cell 'Land_ERG_Vsl' 45), (Cell 'Land_ERG_DurationAVGdischarge' 3600)) }
    AddO 'S05_OTHER_EQUIPMENT/T5_27' ($y + 6) @{ rowKey = 'IGN_DSL'; baseVersion = $null; cells = @((Cell 'Land_No' 1), (Cell 'Land_Status' $running),
            (Cell 'Land_TypeBurnFuelRef' (Entry 'LAND_OTHER_STATIONARY_TYPEOFSTREAMS' 'Diesel')), (Cell 'Con_Region' 'Mangistau - Мангистау'), (Cell 'Land_Measure_IgnitionsNumber_M06' 10)) }

    # golden CSV (необов'язково)
    if ($GoldenDir -and (Test-Path (Join-Path $GoldenDir 'golden-stationary.csv'))) {
        $num = { param($numText) if ([string]::IsNullOrWhiteSpace($numText) -or $numText -eq 'NULL') { return $null }; return [double]::Parse(($numText -replace ',', '.'), [Globalization.CultureInfo]::InvariantCulture) }
        foreach ($r in (Import-Csv (Join-Path $GoldenDir 'golden-stationary.csv') -Delimiter ';' -Encoding UTF8)) {
            $mo = [int]$r.Month
            $ovName = [string]$r.Land_OverhauledEngines
            AddS ($y + $mo) (& $mkStat ('G_' + $r.case_id) $mo $r.Land_TypeEquipment $r.Land_TypeFuel (& $num $r.Land_Power) $r.Land_Rotation $ovName (& $num $r.Land_RunningHoursMonthly) (& $num $r.Fuel_Land_Measure_FuelSubstanceConsumptionOrPumpedVolume) (& $num $r.Land_GasConsumption) (& $num $r.Land_EF_KNO2))
        }
    }
    foreach ($p in @($stat.Keys | Sort-Object)) {
        $ti = TableInstances $docId $p
        $n = Patch-Rows $docId $p $ti['S03_STATIONARY_EQUIPMENT/T3_00'] $stat[$p] "STAT $p"
        Log "    $p T3_00: $n рядків"
    }
    foreach ($p in @($mob.Keys | Sort-Object)) {
        $ti = TableInstances $docId $p
        $n = Patch-Rows $docId $p $ti['S04_MOBILE_EQUIPMENT/T4_01'] $mob[$p] "MOB $p"
        Log "    $p T4_01: $n рядків"
    }
    foreach ($k in @($oth.Keys | Sort-Object)) {
        $tb, $p = $k -split '\|'
        $ti = TableInstances $docId ([int]$p)
        $n = Patch-Rows $docId ([int]$p) $ti[$tb] $oth[$k] "OTH $k"
        Log "    $p ${tb}: $n рядків"
    }
}

# Повний документ: прості дані в усіх таблицях без методологій (щоб кожен аркуш UI мав значення).
function Fill-FullDocument([int]$docId) {
    Log "  наповнення LAND-FULL (id $docId)"
    $first = @{}
    foreach ($r in @($Mdl.registries)) { $first[[string]$r.code] = $r }
    $period = $Year * 100 + 6
    $ti = TableInstances $docId $period
    $total = 0
    foreach ($sh in @($Mdl.sheets)) {
        if ($sh.calc) { continue }
        foreach ($tb in @($sh.tables)) {
            $key = "$($sh.code)/$($tb.code)"
            if (-not $ti.ContainsKey($key)) { continue }
            $cols = @($tb.columns | Where-Object { -not $_.hidden -and -not $_.readOnly -and $_.dataType -in 'Decimal', 'Int', 'String', 'Lookup' -and $_.excel -and -not $_.service } | Select-Object -First 60)
            $mk = {
                param($rowIdx)
                $cells = @()
                foreach ($c in $cols) {
                    $v = $null
                    switch ([string]$c.dataType) {
                        'Decimal' { $mo = if ($c.month) { [int]$c.month } else { 0 }; if ($mo -ge 1 -and $mo -le 10) { $v = [double](($rowIdx * 2) + ($mo % 5) + 1) } elseif ($mo -eq 0) { $v = [double]($rowIdx + 1) } }
                        'Int' { if ([string]$c.code -eq 'No') { $v = $rowIdx + 1 } else { $v = 1 } }
                        'String' { if (-not $c.month) { $v = "Sample $($c.excel)-$rowIdx" } }
                        'Lookup' {
                            $rg = $first[[string]$c.registry]
                            if ($rg -and @($rg.entries).Count -gt 0) { $v = Entry ([string]$c.registry) ([string]@($rg.entries)[0].en) }
                        }
                    }
                    if ($null -ne $v) { $cells += Cell ([string]$c.code) $v }
                }
                return $cells
            }
            $rows = @()
            if ([string]$tb.rowMode -eq 'Dynamic') {
                foreach ($i in 0, 1) { $rows += , @{ rowKey = ('S{0:00}' -f ($i + 1)); baseVersion = $null; cells = (& $mk $i) } }
            }
            else {
                $g = (Api GET "/api/v1/documents/$docId/tables/$($ti[$key])?periodKey=$period").Json
                $i = 0
                foreach ($rw in @($g.rows)) {
                    if ([string]$rw.rowKind -ne 'Item') { continue }
                    $i++
                    $cells = @()
                    foreach ($c in $cols) {
                        if ([string]$c.dataType -eq 'Decimal' -and $c.month -and [int]$c.month -le 10) { $cells += Cell ([string]$c.code) ([double]((($i % 7) + 1) + ($c.month % 3))) }
                    }
                    $tv = $tb.rowValues.PSObject.Properties[[string]$rw.rowKey]
                    $ro = @($tb.columns | Where-Object { $_.readOnly -or $_.hidden } | ForEach-Object { [string]$_.code })
                    if ($null -ne $tv) { foreach ($pv in ($tv.Value.PSObject.Properties | Where-Object { $ro -notcontains $_.Name })) { $cells += Cell ([string]$pv.Name) ([Math]::Round([double]::Parse([string]$pv.Value, [Globalization.CultureInfo]::InvariantCulture), 10)) } }
                    if ($cells.Count -gt 0) { $rows += , @{ rowKey = [string]$rw.rowKey; baseVersion = [string]$rw.rowVersion; cells = $cells } }
                    if ($i -ge 40) { break }
                }
            }
            $n = Patch-Rows $docId $period $ti[$key] $rows $key
            $total += $n
        }
    }
    Log "    рядків у LAND-FULL: $total"
}

& {
    if (Want 'Accounts') { Stage-Accounts }
    if (Want 'Units') { Stage-Units }
    if (Want 'Registries') { Stage-Registries }
    if (Want 'Contract') { Stage-Contract }
    if (Want 'Template') { Stage-Template }
    if (Want 'Project') { Stage-Project }
    if (Want 'Documents') { Stage-Documents }
    # D1: LandParity ПЕРЕД Methodologies (див. нижче).
    if ($Stage -contains 'LandParity') { Stage-LandParity }
    if (Want 'Methodologies') { Stage-Methodologies }
    if (Want 'Data') { Stage-Data }
    # LP-07: LandParity лише явно (-Stage LandParity); у 'All' не входить, бо потребує імпортованих C11/C12 і LAND_AIR_SUBSTANCE.
    # ⚠ D1 (RC E): LandParity виконується ПЕРЕД Methodologies. Methodologies створює прив'язки колонок результатів (LP-08,
    # у т.ч. вимкнені), а перевірка правила категорії (R16-4) звіряє @-аргументи з колонками ВСІХ прив'язок методології:
    # після них PUT category-rule дає 422 ECR-CALC-0422 categoryRuleUnknownArgument.
    # LP-10: лише явно (-Stage CompositionMass); у 'All' не входить, бо потребує імпортованих C11/C12.
    if ($Stage -contains 'CompositionMass') { Stage-CompositionMass }
# 2026-10-13: лише явно (-Stage SubstanceApplicability); у 'All' не входить, бо потребує правил LandParity й довідника.
if ($Stage -contains 'SubstanceApplicability') { Stage-SubstanceApplicability }
    # LP-10: Land_MassFraction - той самий клас підстановки; лише явно (-Stage MassFraction або разом із CompositionMass).
    if ($Stage -contains 'MassFraction' -or $Stage -contains 'CompositionMass') { Stage-MassFraction }
    # C12_02: вибір type1/type2 - лише явно (-Stage ParityBranch); у 'All' не входить; ПІСЛЯ Methodologies (створює формули).
    if ($Stage -contains 'ParityBranch') { Stage-ParityBranch }
}
Log 'Готово.'
