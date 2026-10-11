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

    ⛔ Не запускати проти робочої бази замовника. Скрипт нічого не видаляє. Прод-БД старої системи тут
    НЕ читається (набір із прода — окремий локальний скрипт, не в репозиторії).

.PARAMETER BaseUrl            Адреса API (за замовчуванням http://localhost:5094).
.PARAMETER ModelPath          land-template.model.json.
.PARAMETER AfPackage          Пакет методологій AF (JSON ecr-methodology-package).
.PARAMETER AccountsFile       Файл «користувач<TAB>пароль<TAB>роль» (створюється/доповнюється; поза репозиторієм).
.PARAMETER BootstrapPassword  Пароль bootstrap, якщо його ще немає в -AccountsFile (SecureString).
.PARAMETER GoldenDir          Каталог golden-*.csv (локальні, поза репозиторієм); без нього тести беруться зі спеки (P1..P5).
.PARAMETER Stage              Етапи.
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
    [ValidateSet('Accounts', 'Units', 'Registries', 'Contract', 'Template', 'Project', 'Documents', 'Methodologies', 'Data', 'All')]
    [string[]]$Stage = @('All'),
    [ValidateSet('Sql', 'Api')][string]$StructureMode = 'Sql',
    [string]$SqlConnection = 'Server=localhost;Database=EcrLandUi;Integrated Security=true;TrustServerCertificate=true',
    [string]$TemplateCode = 'LAND_UI',
    [string]$ProjectCode = 'LANDUI2026',
    [int]$Year = 2026,
    [switch]$FillData,
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
Set-StrictMode -Off

# Тимчасові 503 (ECR-SYS-0503 databaseBusy) і 429 повторюємо: стенд ділить SQL Server з іншими сесіями.
${function:Invoke-EcrOnce} = ${function:Invoke-Ecr}
function Invoke-Ecr {
    param([Parameter(Mandatory)][string]$Method, [Parameter(Mandatory)][string]$Path, [Alias('Body')]$Payload = $null, [hashtable]$Headers = @{})
    $r = $null
    foreach ($try in 1..5) {
        $r = Invoke-EcrOnce -Method $Method -Path $Path -Body $Payload -Headers $Headers
        if ($r.Status -ne 503 -and $r.Status -ne 429) { return $r }
        Start-Sleep -Seconds (2 * $try)
    }
    return $r
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
        if ($reg.fieldNames) {
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
        $null = Api PUT "/api/v1/registries/$code/definition" @{ codeMode = $d.codeMode; fields = $fields; keys = @(); rules = @(); reason = 'Land template build' } @{ 'If-Match' = [string]$d.definitionVersion }
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
        if ($reg.fieldNames) { foreach ($p in $e.fields.PSObject.Properties) { $vals[$p.Name] = [string]$p.Value } } else { $vals.NAME = [string]$e.name }
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
    & (Join-Path $here 'Import-ContractDictionaries.ps1') -BaseUrl $BaseUrl -User 'land-owner' -Password $pw
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
    $cmd = $cn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 300
    foreach ($k in $p.Keys) { [void]$cmd.Parameters.AddWithValue('@' + $k, $p[$k]) }
    return $cmd.ExecuteNonQuery()
}
function Bulk-Insert($cn, [string]$table, $dt) {
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
        foreach ($r in (Sql-Query $cn 'SELECT t.Id, s.Code AS S, t.Code AS T FROM cfg.TableDef t JOIN cfg.SheetDef s ON s.Id=t.SheetDefId WHERE s.TemplateVersionId=@v AND t.IsDeleted=0' @{ v = $vid }).Rows) {
            $tableIds["$($r.S)/$($r.T)"] = [int]$r.Id
        }
        foreach ($sh in @($Mdl.sheets)) {
            $to = 0
            foreach ($tb in @($sh.tables)) {
                $to++
                $key = "$($sh.code)/$($tb.code)"
                if (-not $tableIds.ContainsKey($key)) {
                    $mx = [DBNull]::Value; if ($tb.maxRows) { $mx = [int]$tb.maxRows }
                    $id = Sql-Query $cn 'INSERT INTO cfg.TableDef (SheetDefId, Code, NameL10n, Ordinal, LayoutKind, RowMode, MaxDynamicRows, HeaderStyleId, StorageMode, IsDeleted) OUTPUT inserted.Id VALUES (@s,@c,@n,@o,@l,@r,@mx,NULL,0,0)' @{
                        s = $sheetIds[[string]$sh.code]; c = [string]$tb.code; n = (Json-L10n $tb.nameEn $tb.nameRu); o = $to
                        l = [byte]$LayoutCode[[string]$tb.layout]; r = [byte]$RowModeCode[[string]$tb.rowMode]; mx = $mx }
                    $tableIds[$key] = [int]$id.Rows[0].Id
                }
            }
        }
        # --- колонки
        $have = @{}
        foreach ($r in (Sql-Query $cn 'SELECT c.TableDefId, c.Code FROM cfg.ColumnDef c JOIN cfg.TableDef t ON t.Id=c.TableDefId JOIN cfg.SheetDef s ON s.Id=t.SheetDefId WHERE s.TemplateVersionId=@v' @{ v = $vid }).Rows) {
            $have["$($r.TableDefId)/$($r.Code)"] = $true
        }
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
        $haveF = @{}
        foreach ($r in (Sql-Query $cn 'SELECT f.TableDefId, f.ColumnDefId FROM cfg.FormulaDef f JOIN cfg.TableDef t ON t.Id=f.TableDefId JOIN cfg.SheetDef s ON s.Id=t.SheetDefId WHERE s.TemplateVersionId=@v AND f.IsDeleted=0' @{ v = $vid }).Rows) {
            $haveF["$($r.TableDefId)/$($r.ColumnDefId)"] = $true
        }
        $df = New-Object System.Data.DataTable
        foreach ($n in 'TableDefId:int', 'Scope:byte', 'ColumnDefId:int', 'Dialect:byte', 'Expression:string', 'EvaluationOrder:int', 'IsCrossSheet:bool', 'IsSnapshot:bool', 'IsDeleted:bool') {
            $p = $n -split ':'
            $t = switch ($p[1]) { 'int' { [int] } 'byte' { [byte] } 'bool' { [bool] } default { [string] } }
            [void]$df.Columns.Add($p[0], $t)
        }
        $nf = 0
        foreach ($sh in @($Mdl.sheets)) {
            foreach ($tb in @($sh.tables)) {
                $tid = $tableIds["$($sh.code)/$($tb.code)"]
                foreach ($c in @($tb.columns | Where-Object { $_.expression })) {
                    $cid = $colId["$tid/$($c.code)"]
                    if ($haveF.ContainsKey("$tid/$cid")) { continue }
                    $row = $df.NewRow()
                    $row['TableDefId'] = $tid; $row['Scope'] = 0; $row['ColumnDefId'] = $cid; $row['Dialect'] = 0; $row['Expression'] = [string]$c.expression
                    $row['EvaluationOrder'] = 0; $row['IsCrossSheet'] = [bool]([string]$c.expression -match '\]\.\[.*\]\.\[');  $row['IsSnapshot'] = $false; $row['IsDeleted'] = $false
                    $df.Rows.Add($row); $nf++
                }
            }
        }
        Bulk-Insert $cn 'cfg.FormulaDef' $df
        Log "  формул колонок вставлено: $nf"
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
                    defaultValue = $(if ($c.default) { [string]$c.default } else { $null }); displayFormat = $null; styleId = $null
                    lookupRegistryDefId = $lookup; lookupFilter = $null; unitId = (UnitId $c.unit); widthPx = $null
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

    # умовне форматування (cellIs Removed → сірий): одним PUT на версію
    if (@($Mdl.conditionalFormats).Count -gt 0) {
        $g = Invoke-Ecr -Method GET -Path "/api/v1/template-versions/$vid/conditional-formats"
        $etag = [string]$g.Headers['ETag']
        $rules = @($Mdl.conditionalFormats | ForEach-Object { @{ columnCode = [string]$_.columnCode; operator = [string]$_.operator; value = [string]$_.value; valueTo = $null
                    backgroundHex = [string]$_.backgroundHex; foregroundHex = [string]$_.foregroundHex; isBold = [bool]$_.isBold } })
        $h = @{}; if ($etag) { $h['If-Match'] = $etag }
        $cr = Invoke-Ecr -Method PUT -Path "/api/v1/template-versions/$vid/conditional-formats" -Body @{ rules = $rules } -Headers $h
        Log ("  умовне форматування: HTTP {0} {1}" -f $cr.Status, $(if ($cr.Status -ne 200) { ([string]$cr.Text).Substring(0, [Math]::Min(250, ([string]$cr.Text).Length)) } else { '' }))
    }
    # шапка Contract (13 полів) — офіційним скриптом на цій чернетці, з публікацією
    $pw = ConvertTo-SecureString $Acc['land-owner'] -AsPlainText -Force
    & (Join-Path $here 'Apply-ContractHeader.ps1') -TemplateCode $TemplateCode -BaseUrl $BaseUrl -User 'land-owner' -Password $pw -UseExistingDraft
    $vers = @((Api GET "/api/v1/templates/$($S.TemplateId)/versions?limit=100").Json.items)
    $pub = $vers | Where-Object { $_.status -eq 'Published' } | Sort-Object id -Descending | Select-Object -First 1
    if ($null -eq $pub) { throw 'Після Apply-ContractHeader немає опублікованої версії.' }
    $S.VersionId = [int]$pub.id
    Log "  опубліковано версію $($pub.version) (id $($pub.id))"
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
    foreach ($p in 202603, 202604, 202605, 202606) {
        $r = Invoke-Ecr -Method POST -Path "/api/v1/documents/$($byName['LAND-GOLDEN'])/recalculate" -Body @{ periodKey = $p }
        Log ("  перерахунок LAND-GOLDEN ${p}: HTTP $($r.Status)")
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
    if (Want 'Methodologies') { Stage-Methodologies }
    if (Want 'Data') { Stage-Data }
}
Log 'Готово.'
