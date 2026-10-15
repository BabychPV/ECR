<#
.SYNOPSIS
    Library of the FULL-set Land parity harness (dot-sourced by Compare-LandParityFull.ps1). Functions only, no side effects.
.DESCRIPTION
    No production values are written anywhere by this library: cells carry identifiers, classes and relative errors.
    The compare core (Invoke-LandParityCompare) works on in-memory structures, so it is tested on synthetic data.

    Differences from the sample harness (Compare-LandParity.ps1):
      * several periods per document and several legacy EFIDs: rows are aligned BY POSITION, rowKey p<doc>-<seq:0000>
        (the same JSON is the loader input), not by an input signature;
      * methodology -> table: cfg.CalculationBinding (+ -MethodologyTable overrides + built-in fallback);
      * ROW_NOT_LOADED_CAPPED / ROW_NOT_LOADED_NO_CELLS: the legacy row exists, ECR has no row (template row limit /
        row without cells); the same tolerances and the same class list as the sample harness otherwise;
      * AggregateBy NaePhase: an ambiguous key (one NAE+phase for several Land_No) is compared as a SUM over the group.

    Naming rule (LandScriptVariableCollisionTests): no local variable may have the name of a parameter of any
    function/scriptblock in this file, so parameters are named differently from locals and the mutated objects are
    aliased to short locals ($st, $ac).
#>

. (Join-Path $PSScriptRoot 'LandRowKey.Lib.ps1')

function Get-LandParityV {
    # Dictionary-agnostic read (Hashtable and Dictionary[string,object]): null when the key is absent.
    param($Source, [string]$PropName)
    if ($null -eq $Source) { return $null }
    if ($Source -is [System.Collections.IDictionary]) {
        # Hashtable / Dictionary[string,object] have ContainsKey; Dictionary.Contains(x) is a different (KeyValuePair) overload.
        if ($null -ne $Source.PSObject.Methods['ContainsKey']) {
            if ($Source.ContainsKey($PropName)) { return $Source[$PropName] }
        }
        elseif ($Source.Contains($PropName)) { return $Source[$PropName] }
    }
    return $null
}

function Test-LandParityHasKey {
    # D-425: чи є в рядку колонка (ключ) - відрізняє «таблиця без колонки статусу» від «статус порожній».
    param($Source, [string]$PropName)
    if ($null -eq $Source) { return $false }
    if ($Source -is [System.Collections.IDictionary]) {
        if ($null -ne $Source.PSObject.Methods['ContainsKey']) { return $Source.ContainsKey($PropName) }
        return $Source.Contains($PropName)
    }
    return ($null -ne $Source.PSObject.Properties[$PropName])
}

function Test-LandParityNonRunning {
    # D-425: рядок має колонку статусу, і значення не 'Running' (порожнє - теж: легасі такий рядок не рахує ніде).
    param($Row)
    if (-not (Test-LandParityHasKey $Row 'Land_Status')) { return $false }
    $v = Get-LandParityV $Row 'Land_Status'
    return ($null -eq $v -or [string]$v -ne 'Running')
}

function ConvertFrom-LandParityJson {
    # Windows PowerShell 5.1: JavaScriptSerializer (fast on big files). pwsh 6+/Linux: ConvertFrom-Json -AsHashtable.
    param([Parameter(Mandatory)][string]$JsonText)
    if ($PSVersionTable.PSVersion.Major -ge 6) { return ($JsonText | ConvertFrom-Json -AsHashtable) }
    Add-Type -AssemblyName System.Web.Extensions
    $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $ser.MaxJsonLength = [int]::MaxValue
    $ser.RecursionLimit = 200
    return $ser.DeserializeObject($JsonText)
}

function Get-LandParityTableCode {
    # Physical legacy input table -> ECR table code (T3_00, T5_08, T11a_01 ...); $null when not an Air Land table.
    param([string]$PhysicalTable)
    switch -regex ($PhysicalTable) {
        '^AF_Air_Land_Stationary_Equipment$' { return 'T3_00' }
        '^AF_Air_Land_Mobile_Equipment$' { return 'T4_01' }
        '^AF_Air_Land_Other_Equipment_(5_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_Dust_Producing_Activities_(6_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_Utility_(9_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_Off_Utility_DI_(10_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_Off_Utility_AI_(10a_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_Off_Process_DI_(11_\d+)$' { return 'T' + $Matches[1] }
        '^AF_Air_Land_11([abcd])_Off_Process' { return 'T11' + $Matches[1] + '_01' }
        default { return $null }
    }
}

function Get-LandParityMismatchClass {
    # Direction/ratio only: the class never carries the values themselves.
    param([double]$OldValue, [double]$EcrValue)
    if ($EcrValue -eq 0) { return 'MISMATCH_ECR_ZERO' }
    if ($OldValue -eq 0) { return 'MISMATCH_LEGACY_ZERO' }
    $q = $EcrValue / $OldValue
    foreach ($f in 1000.0, 3.6, 3600.0, 100.0, 10.0, 1e6) {
        if ([math]::Abs($q / $f - 1) -lt 1e-4) { return "MISMATCH_RATIO_x$f" }
        if ([math]::Abs($q * $f - 1) -lt 1e-4) { return "MISMATCH_RATIO_div$f" }
    }
    return 'MISMATCH'
}

function Get-LandParityValueClass {
    # Same tolerances as Compare-LandParity.ps1. -StorageTol may be scaled by the caller for sums.
    param([double]$OldValue, [double]$EcrValue, [double]$StorageTol = 5e-13, [double]$RelTol = 1e-12, [double]$AbsTol = 1e-9)
    $diff = [math]::Abs($OldValue - $EcrValue)
    $den = [math]::Max([math]::Abs($OldValue), [math]::Abs($EcrValue))
    $relative = if ($den -eq 0) { 0.0 } else { $diff / $den }
    if ($diff -le $StorageTol -or $relative -le $RelTol) {
        $c = 'MATCH'
        if ($relative -gt 1e-9) { $c = 'MATCH_ROUNDING_LOSS' }
    }
    elseif ($diff -le $AbsTol) { $c = 'NEAR' }
    else { $c = Get-LandParityMismatchClass $OldValue $EcrValue }
    return @{ Class = $c; Abs = $diff; Rel = $relative }
}

function Get-LandParityMissingClass {
    param([hashtable]$StateObject, [string]$MethodologyCode)
    $ms = $StateObject.MethState
    if (-not $ms.ContainsKey($MethodologyCode)) { return 'MISSING_NOT_IN_ECR' }
    if ($ms[$MethodologyCode][0] -eq 0) { return 'MISSING_NOT_PUBLISHED' }
    if ($ms[$MethodologyCode][1] -eq 0) {
        # Опублікована методологія з прив'язками, УСІ IsActive=0 - окремий клас від справжньої відсутності прив'язки (P3).
        if ($ms[$MethodologyCode].Count -gt 2 -and [int]$ms[$MethodologyCode][2] -eq 1) { return 'MISSING_BINDING_INACTIVE' }
        return 'MISSING_NO_BINDING'
    }
    return 'MISSING_BOUND_NO_RESULT'
}

function ConvertTo-LandParityMethodologyTableMap {
    # -MethodologyTable entries: '<regex>=<TableCode>[+<TableCode>...]', e.g. '^ECW_C12_01$=T11_02+T11a_01'.
    param([string[]]$Entry)
    $items = New-Object System.Collections.ArrayList
    foreach ($raw in @($Entry)) {
        foreach ($e in ([string]$raw -split ';')) {
            if ([string]::IsNullOrWhiteSpace($e)) { continue }
            $i = $e.LastIndexOf('=')
            if ($i -lt 1 -or $i -eq $e.Length - 1) { throw "MethodologyTable entry '$e' must be <regex>=<TableCode>[+<TableCode>]." }
            $rx = $e.Substring(0, $i).Trim()
            $codes = @($e.Substring($i + 1).Split('+') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            foreach ($t in $codes) {
                if ($t -notmatch '^T\d+[a-d]?_\d+$') { throw "MethodologyTable entry '$e': '$t' is not a table code (T3_00, T5_08, T11a_01 ...)." }
            }
            try { [void][regex]::new($rx) } catch { throw "MethodologyTable entry '$e': bad regex ($($_.Exception.Message))." }
            [void]$items.Add(@{ Regex = $rx; Tables = $codes })
        }
    }
    return , @($items.ToArray())
}

function ConvertTo-LandParityGroupByMap {
    # -GroupBy entries: '<TableCode>=<Column>[+<Column>...]' or '<Column>[+<Column>]' (all tables), e.g. 'T11_02=Land_TypeFuel'.
    # Result: hashtable TableCode|'*' -> string[] columns. The FIRST column is also the legacy Component_Phase filter.
    param([string[]]$Entry)
    $map = @{}
    foreach ($raw in @($Entry)) {
        foreach ($e in ([string]$raw -split ';')) {
            if ([string]::IsNullOrWhiteSpace($e)) { continue }
            $tbl = '*'; $colsPart = $e.Trim()
            $i = $e.IndexOf('=')
            if ($i -ge 0) { $tbl = $e.Substring(0, $i).Trim(); $colsPart = $e.Substring($i + 1).Trim() }
            if ($tbl -ne '*' -and $tbl -notmatch '^T\d+[a-d]?_\d+$') { throw "GroupBy entry '$e': '$tbl' is not a table code." }
            $cols = @($colsPart.Split('+') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            if ($cols.Count -eq 0) { throw "GroupBy entry '$e' has no columns." }
            foreach ($c in $cols) { if ($c -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "GroupBy entry '$e': bad column name '$c'." } }
            $map[$tbl] = $cols
        }
    }
    return $map
}

# Built-in fallback methodology -> table (the 6 patterns of the sample harness, as table codes).
$script:LandParityDefaultMethodologyTable = @(
    @{ Regex = '^ECW_C05_'; Tables = @('T3_00') }
    @{ Regex = '^ECW_C06_01'; Tables = @('T4_01') }
    @{ Regex = '^ECW_C07_07'; Tables = @('T5_08') }
    @{ Regex = '^ECW_C07_16'; Tables = @('T5_22') }
    @{ Regex = '^ECW_C07_08'; Tables = @('T5_09') }
    @{ Regex = '^ECW_C07_21'; Tables = @('T5_27') }
    # C12_02 (газові турбіни; GTU) і C12_03 (холодний вентиль): таблиці з land-result-outputs.json. Кілька таблиць -
    # результат належить таблиці, чий рядок завантажено (Resolve-LandParityResultTables).
    @{ Regex = '^ECW_C12_02$'; Tables = @('T10_03', 'T11_03') }
    @{ Regex = '^ECW_C12_03$'; Tables = @('T10_01', 'T11_01') }
)

# Нормалізація імен виходів ECR до ключа старої системи (`ECW_EC_<tons|gsec>_<код>`; старий код складає вихід із
# префікса й коду речовини, а гілку type1/type2 знімає: C12_02 `Replace("{type}", type)`, C12_03 `XXX -> code`).
#   * `ECW_EC_tons_XXX` + речовина запису довідника (SUBSTANCE_CODE) -> `ECW_EC_tons_<код речовини>`;
#   * `ECW_EC_tons_<код>_type1|_type2` у методології зі списку TypeFold -> `ECW_EC_tons_<код>`; обидві гілки разом
#     для одного ключа - BRANCH_BOTH_PRESENT (старий код пише лише одну; ParityBranch мав лишити одну);
#   * type1 коду зі списку NullType1 (415: у старому коді не існує) лишається ПОВНИМ іменем -> ECR_ONLY.
# Значень тут немає: лише імена.
$script:LandParityTypeFoldDefault = @('^ECW_C12_02$')
$script:LandParityNullType1Default = @{ 'ECW_C12_02' = @('415') }

function Get-LandParityTypeFoldDefault { return @($script:LandParityTypeFoldDefault) }

# Селектор гілки (тест-харнес, без зміни продукту): легасі рахує ОДНУ гілку на (рядок, вихід) за NAE/потоком, ECR - усі.
# Для методологій зі списку значення гілок зберігаються окремо (EcrVar), порівнюється лише гілка, яку обрав би легасі,
# решта гілок - клас ECR_ONLY_NOT_SELECTED (не ECR_ONLY і не BRANCH_BOTH_PRESENT). Методологія зі списку знімає гілку
# (fold) без -TypeFoldMethodology. Правила - у Get-LandParitySelectedBranch (джерела ECW_C11_*.cs у docs/source).
$script:LandParityBranchSelectorDefault = @('ECW_C11_01', 'ECW_C11_02', 'ECW_C11_11_02', 'ECW_C11_11_03')

function Get-LandParityBranchSelectorDefault { return @($script:LandParityBranchSelectorDefault) }

# ECW_C11_02.cs:143-146 typeCalculationGroup (точне List.Contains по NAE без пробілів: `6583 (002)` зі списку не
# збігається ніколи - легасі так само дає «Error formula's type», результату немає).
$script:LandParityC1102Type1 = @('6640', '6641', '6642', '6643', '6644', '6645', '6582(002)', '6583 (002)', '6585(001)', '6585(003)',
    '6585(004)', '6586(002)', '6586(003)', '6586(004)', '6587(001)', '6587(003)', '6587(004)', '6592(002)', '6592(003)', '6592(004)')
$script:LandParityC1102Type2 = @('6783(001)', '6786(001)', '6787(001)', '6789(001)')
# ECW_C11_11_03.cs:81-90: окремі групи для tons і gsec (tons: type1/type2/type3; gsec: type1/type2 - type3 легасі зливає в type2).
$script:LandParityC111103Tons = [ordered]@{
    'type1' = @('0800(001)', '0800(002)', '0800(003)', '0800(004)', '0800(005)', '0800(006)', '0800(007)', '6800')
    'type2' = @('0782(001)', '0782(002)', '0782(003)', '0782(004)', '0782(005)', '0782(006)', '0782(007)', '0782(008)', '6784')
    'type3' = @('0804', '6801', '6802', '6803')
}
$script:LandParityC111103Gsec = [ordered]@{
    'type1' = @('0800(001)', '0800(002)', '0800(003)', '0800(004)', '0800(005)', '0800(006)', '0800(007)', '6800')
    'type2' = @('0782(001)', '0782(002)', '0782(003)', '0782(004)', '0782(005)', '0782(006)', '0782(007)', '0782(008)', '6784',
        '0804', '6801', '6802', '6803')
}
# ECW_C11_11_02.cs:109-124 groupNAE (NAE, що взагалі обробляються) і :127-133 group1[6975] (type2).
$script:LandParityC111102Known = @('0788', '0789', '0790', '0791', '0788(001)', '0789(001)', '0790(001)', '0791(001)',
    '6975', '6981', '6982', '6983', '6984', '6985', '6986', '6976', '0560', '0562', '0563', '0561', '0564', '0565', '0903', '0905',
    '6904', '0793', '0883', '0524')
$script:LandParityC111102Type2 = @('6975', '6981', '6982', '6983', '6984', '6985', '6986', '6976', '0883')

function Get-LandParitySelectedBranch {
    <#
    .SYNOPSIS  Гілка, яку обрав би легасі для рядка: 'type1' | 'type1_1' | 'type1_2' | 'type1_3' | 'type2' | 'type3'; $null - легасі
               для рядка нічого не рахує (NAE поза групами) або методології немає в селекторі.
    .DESCRIPTION
        ECW_C11_01.cs:176-208: NAE (Trim, без пробілів) містить 6646..6657 -> type2; інакше потік FG/LPG -> type1_1,
        Diesel/452 -> type1_2, решта -> type1_3. Потік: Land_TypeFuelOrStream (інакше Land_TypeFuel), правила :109-119
        (gas/Gas -> FG, LPG, sludge, caustic, water -> Wwater, 401, 14, 452, Diesel; пізніший збіг перемагає; порожній потік
        = сам рядок аргументу).
        ECW_C11_02.cs:143-146,357-361: лише за NAE (точний збіг зі списком).
        ECW_C11_11_02.cs:221-239: NAE 0883 або група 6975 -> type2, інакше type1 (NAE має бути в groupNAE).
        ECW_C11_11_03.cs:81-112: окремі групи для tons і gsec (MetricName).
    #>
    param([string]$MethodologyCode, [string]$MetricName, $Row)
    $nae = [string](Get-LandParityV $Row 'Land_NAE')
    $nae = $nae.Trim().Replace(' ', '')
    switch ($MethodologyCode) {
        'ECW_C11_01' {
            foreach ($n in 6646..6657) { if ($nae.Contains([string]$n)) { return 'type2' } }
            $arg = [string](Get-LandParityV $Row 'Land_TypeFuelOrStream')
            # Фолбек на Land_TypeFuel - НЕ з легасі (там лише потік): евристика harness для рядків без Land_TypeFuelOrStream.
            if ($arg -eq '') { $arg = [string](Get-LandParityV $Row 'Land_TypeFuel') }
            $s = ''
            if ($arg.Contains('gas') -or $arg.Contains('Gas')) { $s = 'FG' }
            if ($arg.Contains('LPG')) { $s = 'LPG' }
            if ($arg.Contains('sludge')) { $s = 'sludge' }
            if ($arg.Contains('caustic')) { $s = 'caustic' }
            if ($arg.Contains('water')) { $s = 'Wwater' }
            if ($arg.Contains('401')) { $s = '401' }
            if ($arg.Contains('14')) { $s = '14' }
            if ($arg.Contains('452')) { $s = '452' }
            if ($arg.Contains('Diesel')) { $s = 'Diesel' }
            if ($s -eq '') { $s = $arg }
            if ($s -ceq 'FG' -or $s -ceq 'LPG') { return 'type1_1' }
            if ($s -ceq 'Diesel' -or $s -ceq '452') { return 'type1_2' }
            return 'type1_3'
        }
        'ECW_C11_02' {
            if ($script:LandParityC1102Type1 -ccontains $nae) { return 'type1' }
            if ($script:LandParityC1102Type2 -ccontains $nae) { return 'type2' }
            return $null
        }
        'ECW_C11_11_02' {
            if (-not ($script:LandParityC111102Known -ccontains $nae)) { return $null }
            if ($script:LandParityC111102Type2 -ccontains $nae) { return 'type2' }
            return 'type1'
        }
        'ECW_C11_11_03' {
            $groups = $script:LandParityC111103Gsec
            if ($MetricName -eq 'tons') { $groups = $script:LandParityC111103Tons }
            foreach ($t in $groups.Keys) { if (@($groups[$t]) -ccontains $nae) { return $t } }
            return $null
        }
    }
    return $null
}

function Get-LandParityNullType1Default {
    $copy = @{}
    foreach ($k in $script:LandParityNullType1Default.Keys) { $copy[$k] = @($script:LandParityNullType1Default[$k]) }
    return $copy
}

function ConvertTo-LandParityCanonicalOutput {
    <#
    .OUTPUTS  @{ Code; Variant; Ignore } - Code: ключ виходу (як у старої системи, якщо перетворення можливе); Variant: 'type1'|'type2'|''
              (лише якщо гілку знято); Ignore: результат речовини, чужої для виходу з кодом у імені (не порівнюється).
    #>
    param([hashtable]$StateObject, [string]$MethodologyCode, [string]$OutputCode, [string]$SubstanceCode = '')
    $m = [regex]::Match($OutputCode, '^ECW_EC_(?<metric>tons|gsec)_(?<id>XXX|[0-9]+)(?:_(?<variant>type[0-9]+(?:_[0-9]+)?))?$')
    $raw = @{ Code = $OutputCode; Variant = ''; Ignore = $false }
    if (-not $m.Success) { return $raw }
    $metric = $m.Groups['metric'].Value
    $codeId = $m.Groups['id'].Value
    $branch = $m.Groups['variant'].Value
    if ($codeId -eq 'XXX') {
        if ([string]::IsNullOrWhiteSpace($SubstanceCode)) { return $raw }   # речовини немає: ім'я лишається XXX
        $codeId = $SubstanceCode.Trim()
    }
    elseif (-not [string]::IsNullOrWhiteSpace($SubstanceCode) -and $SubstanceCode.Trim() -ne $codeId) {
        # Вихід із кодом у імені, порахований у проході ІНШОЇ речовини: значення того ж проходу, що й власна речовина.
        $raw.Ignore = $true
        return $raw
    }
    if ($branch -eq '') { return @{ Code = "ECW_EC_${metric}_$codeId"; Variant = ''; Ignore = $false } }

    $fold = $false
    foreach ($rx in @($StateObject.TypeFold)) { if ($MethodologyCode -match $rx) { $fold = $true; break } }
    if (@($StateObject.BranchSelector) -contains $MethodologyCode) { $fold = $true }
    if (-not $fold) { return @{ Code = "ECW_EC_${metric}_${codeId}_$branch"; Variant = ''; Ignore = $false } }
    if ($branch -eq 'type1' -and $StateObject.NullType1.ContainsKey($MethodologyCode) -and @($StateObject.NullType1[$MethodologyCode]) -contains $codeId) {
        return @{ Code = "ECW_EC_${metric}_${codeId}_$branch"; Variant = ''; Ignore = $false }
    }
    return @{ Code = "ECW_EC_${metric}_$codeId"; Variant = $branch; Ignore = $false }
}

function Get-LandParityBucket {
    <#
    .SYNOPSIS  Клас комірки -> одна з груп звіту: MATCHED | MISMATCH | ECR_ONLY | ORACLE_ONLY | OTHER.
    .DESCRIPTION
        MATCHED: MATCH, MATCH_ROUNDING_LOSS, а також NEAR і MISMATCH* з відносною похибкою не більшою за поріг
        (Within = TRUE: збіг лише завдяки порогу). ORACLE_ONLY: у старій системі є, в ECR немає (MISSING_*, ROW_NOT_LOADED_*,
        METHODOLOGY_DIFF). EXPLAINED_SKIP: LEGACY_STATUS_SKIP (легасі пропускає не-Running рядок) - окремий лічильник. OTHER: невизначеність ключа/гілки (AMBIGUOUS_KEY, LEGACY_DUP_CONFLICT,
        INCOMPLETE_*, BRANCH_BOTH_PRESENT, AMBIGUOUS_ECR_KEY) - значення не порівнювалось.
    #>
    param([string]$ClassName, [double]$RelErr, [double]$Threshold)
    $core = $ClassName -replace '^AGG_', '' -replace '_ZERO$', ''
    $zero = ($ClassName -match '_ZERO$') -and ($ClassName -notlike '*MISMATCH*')
    $group = 'OTHER'; $within = $false
    if ($core -eq 'MATCH' -or $core -eq 'MATCH_ROUNDING_LOSS') { $group = 'MATCHED' }
    elseif ($core -eq 'ECR_ONLY_NOT_SELECTED') { $group = 'OTHER' }   # гілка, якої легасі не обирає: не ECR_ONLY і не збіг
    elseif ($core -eq 'NEAR' -or $core -like 'MISMATCH*') {
        if ($RelErr -le $Threshold) { $group = 'MATCHED'; $within = $true } else { $group = 'MISMATCH' }
    }
    elseif ($core -like 'ECR_ONLY*' -or $core -eq 'ECR_NON_RUNNING') { $group = 'ECR_ONLY' }
    # EXPLAINED_SKIP: legacy behaviour 9 (non-Running row is skipped by the old engine) - explained, not a real oracle-only gap.
    elseif ($core -eq 'LEGACY_STATUS_SKIP') { $group = 'EXPLAINED_SKIP' }
    elseif ($core -like 'MISSING_*' -or $core -like 'ROW_NOT_LOADED*' -or $core -eq 'METHODOLOGY_DIFF') { $group = 'ORACLE_ONLY' }
    return @{ Group = $group; Within = $within; Zero = $zero }
}

function New-LandParityState {
    return @{
        Ecr = @{}            # "doc|period|table|rowKey" -> @{ "meth|output" = double }
        EcrMeth = @{}        # "doc|period|table|meth" -> 1
        RowAny = @{}         # "doc|period|table|rowKey" -> 1
        Loaded = New-Object 'System.Collections.Generic.HashSet[string]'   # "doc|period|table|rowKey"
        LoadedCnt = @{}      # "doc|period|table" -> rows
        MaxRows = @{}        # table -> MaxDynamicRows
        MethState = @{}      # methodology code -> @(published, bound)
        MethTables = @{}     # methodology code -> string[] table codes (active bindings)
        MethodologyTable = @()   # overrides (ConvertTo-LandParityMethodologyTableMap)
        MultiTable = @{}     # methodology -> results attributed to several candidate tables
        ResultCount = 0
        TypeFold = @(Get-LandParityTypeFoldDefault)         # regex методологій, де гілку type1/type2 знімають
        NullType1 = (Get-LandParityNullType1Default)        # методологія -> коди, чий type1 у старому коді не існує
        BranchSelector = (Get-LandParityBranchSelectorDefault)   # методології, де легасі обирає гілку за NAE/потоком ($null/@() - вимкнено)
        EcrVar = @{}         # "doc|period|table|rowKey|meth|output" -> @{ <гілка> = double }: значення за гілками (без "last wins")
        Variants = @{}       # "doc|period|table|rowKey|meth|output" -> @{ type1 = 1; type2 = 1 }
        BranchBoth = @{}     # той самий ключ -> 1: обидві гілки мають результат
        NoSubstance = @{}    # methodology -> результати ECW_EC_*_XXX без коду речовини
        ForeignSubstance = @{}   # methodology -> результати з кодом у імені, порахованими в проході чужої речовини
        TableKeyDocs = @{}       # документ -> 1: у ньому є рядки з ключем формату Table (p<doc>-<TableCode>-<seq>)
        AmbiguousEcr = @{}       # "doc|period|table|rowKey|meth|output" -> 1: старий ключ, кілька таблиць, РІЗНІ значення (AMBIGUOUS_ECR_KEY)
        MultiWritten = @{}       # той самий ключ -> 1: значення записане під час приписування кільком таблицям
        AmbiguousByMeth = @{}    # methodology -> кількість неоднозначних ключів
        KeyFormats = @{ Table = 0; Legacy = 0; Unknown = 0 }   # результати ECR за форматом SourceRowKey
        ResultsWithTableInstance = 0      # результати з TableInstanceId -> код таблиці (ключ порівняння: таблиця + rowKey)
        ResultsWithoutTableInstance = 0   # TableInstanceId NULL: стара поведінка (Resolve-LandParityResultTables)
    }
}

function Add-LandParityLoadedRow {
    # The only way to register a loaded document row: also remembers which documents use table-qualified keys.
    param([hashtable]$StateObject, [string]$DocumentName, [int]$PeriodKey, [string]$TableCode, [string]$RowKey)
    $stl = $StateObject
    [void]$stl.Loaded.Add("$DocumentName|$PeriodKey|$TableCode|$RowKey")
    $docAlias = $DocumentName   # alias: LandScriptVariableCollisionTests does not allow an element keyed by a parameter on the left
    if ((ConvertFrom-LandRowKey $RowKey).Format -eq 'Table') { $stl.TableKeyDocs[$docAlias] = 1 }
}

function Resolve-LandParityResultTables {
    param([hashtable]$StateObject, [string]$DocumentName, [int]$PeriodKey, [string]$MethodologyCode, [string]$RowKey)
    $cands = @()
    if ($StateObject.MethTables.ContainsKey($MethodologyCode)) { $cands = @($StateObject.MethTables[$MethodologyCode]) }
    if ($cands.Count -eq 0) {
        foreach ($m in @($StateObject.MethodologyTable) + @($script:LandParityDefaultMethodologyTable)) {
            if ($MethodologyCode -match $m.Regex) { $cands = @($m.Tables); break }
        }
    }
    # Table-qualified key (p<doc>-<TableCode>-<seq>): the result belongs to exactly that table, no guessing.
    $pk = ConvertFrom-LandRowKey $RowKey
    if ($pk.Format -eq 'Table') { return @($pk.Table) }
    if ($cands.Count -eq 0) { return @('(unmapped)') }
    if ($cands.Count -eq 1) { return $cands }
    # Legacy key p<doc>-<seq> carries no table: keep the candidates whose document row exists.
    $hit = @($cands | Where-Object { $StateObject.Loaded.Contains("$DocumentName|$PeriodKey|$_|$RowKey") })
    if ($hit.Count -eq 0) { $hit = $cands }
    if ($hit.Count -gt 1) {
        $mt = $StateObject.MultiTable
        $mcode = $MethodologyCode
        if (-not $mt.ContainsKey($mcode)) { $mt[$mcode] = 0 }
        $mt[$mcode]++
    }
    return $hit
}

function Add-LandParityEcrResult {
    param([hashtable]$StateObject, [string]$DocumentName, [int]$PeriodKey, [string]$MethodologyCode, [string]$RowKey, [string]$OutputCode, [double]$Value, [string]$SubstanceCode = '', [string]$TableCode = '')
    $st = $StateObject
    $cn = ConvertTo-LandParityCanonicalOutput $st $MethodologyCode $OutputCode $SubstanceCode
    $mname = $MethodologyCode   # alias: LandScriptVariableCollisionTests does not allow assigning an element keyed by a parameter
    if ($cn.Ignore) {
        if (-not $st.ForeignSubstance.ContainsKey($mname)) { $st.ForeignSubstance[$mname] = 0 }
        $st.ForeignSubstance[$mname]++
        return
    }
    if ($OutputCode -match '_XXX(_|$)' -and $cn.Code -eq $OutputCode) {
        if (-not $st.NoSubstance.ContainsKey($mname)) { $st.NoSubstance[$mname] = 0 }
        $st.NoSubstance[$mname]++
    }
    $fmt = (ConvertFrom-LandRowKey $RowKey).Format
    $st.KeyFormats[$fmt] = 1 + [int]$st.KeyFormats[$fmt]
    # TableInstanceId результату -> код таблиці: ключ порівняння (table, rowKey); без нього легасі-ключ p<doc>-<seq>
    # колізує між таблицями одного документа (BRANCH_BOTH_PRESENT / AMBIGUOUS_ECR_KEY).
    if (-not [string]::IsNullOrWhiteSpace($TableCode)) {
        $targets = @($TableCode.Trim())
        $st.ResultsWithTableInstance++
    }
    else {
        $targets = @(Resolve-LandParityResultTables $st $DocumentName $PeriodKey $MethodologyCode $RowKey)
        $st.ResultsWithoutTableInstance++
    }    $multiTarget = ($targets.Count -gt 1)
    foreach ($tc in $targets) {
        $mkey = "$DocumentName|$PeriodKey|$tc|$MethodologyCode"
        $st.EcrMeth[$mkey] = 1
        $k = "$DocumentName|$PeriodKey|$tc|$RowKey"
        $outKey = "$MethodologyCode|$($cn.Code)"
        if (-not $st.Ecr.ContainsKey($k)) { $st.Ecr[$k] = @{} }
        # Legacy key shared by several candidate tables: a second DIFFERENT value for the same cell is a key collision,
        # not an update. Flag it (AMBIGUOUS_ECR_KEY) instead of "last wins". Values are never printed.
        $ak = "$k|$outKey"
        if ($fmt -ne 'Table' -and $st.Ecr[$k].ContainsKey($outKey) -and ($multiTarget -or $st.MultiWritten.ContainsKey($ak)) -and
            [double]$st.Ecr[$k][$outKey] -ne $Value) {
            if (-not $st.AmbiguousEcr.ContainsKey($ak)) {
                $st.AmbiguousEcr[$ak] = 1
                $st.AmbiguousByMeth[$mname] = 1 + [int]$st.AmbiguousByMeth[$mname]
            }
        }
        if ($multiTarget) { $st.MultiWritten[$ak] = 1 }
        $st.Ecr[$k][$outKey] = $Value
        $st.RowAny[$k] = 1
        if ($cn.Variant -ne '') {
            $vk = "$k|$outKey"
            if (-not $st.Variants.ContainsKey($vk)) { $st.Variants[$vk] = @{} }
            $st.Variants[$vk][$cn.Variant] = 1
            if (-not $st.EcrVar.ContainsKey($vk)) { $st.EcrVar[$vk] = @{} }
            $st.EcrVar[$vk][$cn.Variant] = $Value
            if ($st.Variants[$vk].Count -gt 1) { $st.BranchBoth[$vk] = 1 }
        }
    }
    $st.ResultCount++
}

function Import-LandParityStateFile {
    # Offline state (tests, archived runs). JSON:
    #  methodologies: [{code, published, bound, anyBinding (є прив'язки, хай усі IsActive=0), tables: ["T3_00"]}], tables: [{document, period, table, rowKeys: [..], maxRows}],
    #  results: [{document, period, methodology, rowKey, outputCode, value, substanceCode, table (код таблиці з TableInstanceId; порожньо = NULL)}]
    param([Parameter(Mandatory)][string]$Path, [hashtable]$TargetState)
    $st = $TargetState
    $j = ConvertFrom-LandParityJson ([System.IO.File]::ReadAllText($Path))
    $inv = [Globalization.CultureInfo]::InvariantCulture
    foreach ($m in @(Get-LandParityV $j 'methodologies')) {
        $code = [string](Get-LandParityV $m 'code')
        $st.MethState[$code] = @([int](Get-LandParityV $m 'published'), [int](Get-LandParityV $m 'bound'), [int](Get-LandParityV $m 'anyBinding'))
        $names = @(Get-LandParityV $m 'tables' | ForEach-Object { [string]$_ })
        if ($names.Count -gt 0) { $st.MethTables[$code] = $names }
    }
    foreach ($t in @(Get-LandParityV $j 'tables')) {
        $dn = [string](Get-LandParityV $t 'document'); $pk = [int](Get-LandParityV $t 'period'); $tc = [string](Get-LandParityV $t 'table')
        $cnt = 0
        foreach ($rk in @(Get-LandParityV $t 'rowKeys')) { Add-LandParityLoadedRow $st $dn $pk $tc ([string]$rk); $cnt++ }
        $st.LoadedCnt["$dn|$pk|$tc"] = $cnt
        $mx = Get-LandParityV $t 'maxRows'
        if ($null -ne $mx) { $st.MaxRows[$tc] = [int]$mx }
    }
    foreach ($res in @(Get-LandParityV $j 'results')) {
        Add-LandParityEcrResult $st ([string](Get-LandParityV $res 'document')) ([int](Get-LandParityV $res 'period')) `
            ([string](Get-LandParityV $res 'methodology')) ([string](Get-LandParityV $res 'rowKey')) ([string](Get-LandParityV $res 'outputCode')) `
            ([double]::Parse([string](Get-LandParityV $res 'value'), $inv)) ([string](Get-LandParityV $res 'substanceCode')) ([string](Get-LandParityV $res 'table'))
    }
}

function Import-LandParityStateSql {
    # Read-only SELECTs under READ UNCOMMITTED against a local EcrLand* stand database.
    param([Parameter(Mandatory)][string]$ConnectionString, [hashtable]$TargetState)
    $st = $TargetState
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $rd = {
        param([string]$SqlText, [scriptblock]$PerRow)
        $cn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString); $cn.Open()
        try {
            $cmd = $cn.CreateCommand(); $cmd.CommandTimeout = 600
            $cmd.CommandText = "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; $SqlText"
            $reader = $cmd.ExecuteReader()
            try { while ($reader.Read()) { & $PerRow $reader } } finally { $reader.Close() }
        }
        finally { $cn.Close() }
    }
    & $rd "SELECT m.Code, CASE WHEN EXISTS (SELECT 1 FROM calc.MethodologyVersion v WHERE v.MethodologyId = m.Id AND v.Status = 1) THEN 1 ELSE 0 END, CASE WHEN EXISTS (SELECT 1 FROM cfg.CalculationBinding b WHERE b.MethodologyId = m.Id AND b.IsActive = 1) THEN 1 ELSE 0 END, CASE WHEN EXISTS (SELECT 1 FROM cfg.CalculationBinding b WHERE b.MethodologyId = m.Id) THEN 1 ELSE 0 END FROM calc.Methodology m" {
        param($rec) $code = [string]$rec[0]; $st.MethState[$code] = @([int]$rec[1], [int]$rec[2], [int]$rec[3])
    }
    $byCode = @{}
    & $rd "SELECT DISTINCT m.Code, td.Code FROM cfg.CalculationBinding b JOIN calc.Methodology m ON m.Id = b.MethodologyId JOIN cfg.TableDef td ON td.Id = b.TableDefId WHERE b.IsActive = 1" {
        param($rec) $k = [string]$rec[0]; if (-not $byCode.ContainsKey($k)) { $byCode[$k] = New-Object System.Collections.ArrayList }; [void]$byCode[$k].Add([string]$rec[1])
    }
    foreach ($k in $byCode.Keys) { $st.MethTables[$k] = @($byCode[$k].ToArray()) }
    & $rd "SELECT JSON_VALUE(d.NameL10n, '$.en'), tr.PeriodKey, td.Code, tr.RowKey, td.MaxDynamicRows FROM doc.TableRow tr JOIN doc.TableInstance ti ON ti.Id = tr.TableInstanceId AND ti.PeriodKey = tr.PeriodKey JOIN doc.Document d ON d.Id = ti.DocumentId JOIN cfg.TableDef td ON td.Id = ti.TableDefId WHERE tr.IsDeleted = 0" {
        param($rec)
        $tk = "$([string]$rec[0])|$([int]$rec[1])|$([string]$rec[2])"
        Add-LandParityLoadedRow $st ([string]$rec[0]) ([int]$rec[1]) ([string]$rec[2]) ([string]$rec[3])
        $st.LoadedCnt[$tk] = 1 + [int]$st.LoadedCnt[$tk]
        $tcode = [string]$rec[2]
        if (-not $rec.IsDBNull(4)) { $st.MaxRows[$tcode] = [int]$rec[4] }
    }
    # Код речовини результату: поле SUBSTANCE_CODE запису довідника (як MethodologyStore.GetSubstanceCodesAsync), інакше код запису.
    $substanceSql = "COALESCE((SELECT TOP 1 COALESCE(NULLIF(LTRIM(RTRIM(v.ValueString)), ''), FORMAT(v.ValueNumeric, '0.#########')) FROM dic.RegistryValue v JOIN cfg.RegistryFieldDef f ON f.Id = v.RegistryFieldDefId AND f.Code = 'SUBSTANCE_CODE' WHERE v.RegistryEntryId = cr.SubstanceEntryId), se.Code)"
    & $rd "SELECT JSON_VALUE(d.NameL10n, '$.en'), cr.PeriodKey, m.Code, cr.SourceRowKey, cr.OutputCode, cr.Value, $substanceSql, td.Code FROM calc.CalculationResult cr LEFT JOIN doc.TableInstance cti ON cti.Id = cr.TableInstanceId LEFT JOIN cfg.TableDef td ON td.Id = cti.TableDefId JOIN calc.CalculationRun r ON r.Id = cr.CalculationRunId AND r.Status = 'Current' JOIN calc.MethodologyVersion mv ON mv.Id = cr.MethodologyVersionId JOIN calc.Methodology m ON m.Id = mv.MethodologyId JOIN doc.Document d ON d.Id = cr.DocumentId LEFT JOIN dic.RegistryEntry se ON se.Id = cr.SubstanceEntryId" {
        param($rec)
        Add-LandParityEcrResult $st ([string]$rec[0]) ([int]$rec[1]) ([string]$rec[2]) ([string]$rec[3]) ([string]$rec[4]) ([double]::Parse([string]$rec[5], $inv)) ([string]$rec[6]) ($(if ($rec.IsDBNull(7)) { '' } else { [string]$rec[7] }))
    }
}

function Import-LandParityOracleTsv {
    # Columns: efid month landTable nae component phase tons gsec methodology modified [source]. LOCAL file with values.
    param([Parameter(Mandatory)][string]$Path)
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $orc = @{}
    foreach ($ln in [System.IO.File]::ReadLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($ln)) { continue }
        $f = $ln.Split("`t")
        if ($f.Count -lt 10) { continue }
        $k = "$($f[0])|$([int]$f[1])|$($f[2])|$($f[3].Trim())"
        if (-not $orc.ContainsKey($k)) { $orc[$k] = New-Object System.Collections.ArrayList }
        $tons = $null; $gsec = $null
        if ($f[6] -ne '') { $tons = [double]::Parse($f[6], $inv) }
        if ($f[7] -ne '') { $gsec = [double]::Parse($f[7], $inv) }
        [void]$orc[$k].Add(@{ component = $f[4]; phase = $f[5]; tons = $tons; gsec = $gsec; methodology = $f[8]; modified = $f[9] })
    }
    return $orc
}

function New-LandParityAccumulator {
    param([double]$RelThreshold = 1e-9)
    return @{
        RelThreshold = $RelThreshold
        Bucket = @{}                                # "meth|substance" -> счётчики груп звіту (matched/mismatch/...)
        Agg = @{}                                   # "meth|table|class" -> counters
        Rels = @{}                                  # same key -> List[double]
        Detail = New-Object 'System.Collections.Generic.List[string]'
        Docs = @{}; Periods = @{}
        AggregateGroups = 0
        BranchSelected = 0       # клітинки, порівняні за гілкою, яку обирає легасі (селектор гілки)
        BranchNotSelected = 0    # клітинки ECR_ONLY_NOT_SELECTED
        Warnings = New-Object 'System.Collections.Generic.List[string]'   # напр. відсутня колонка -GroupBy
        WarnedKeys = @{}
    }
}

function Add-LandParityCell {
    param([hashtable]$Accumulator, [string]$DocumentName, [int]$PeriodKey, [string]$TableCode, [string]$RowKey, [int]$RowCount, [string]$EfidValue,
        [string]$MethodologyCode, [string]$OutputLabel, [string]$ClassName, [double]$RelErr = 0.0)
    $ac = $Accumulator
    $k = "$MethodologyCode|$TableCode|$ClassName"
    if (-not $ac.Agg.ContainsKey($k)) {
        $ac.Agg[$k] = @{ Methodology = $MethodologyCode; Table = $TableCode; Class = $ClassName; Cells = 0; Rows = 0; Docs = @{}; Periods = @{}; Sample = $null }
    }
    $a = $ac.Agg[$k]
    $dn = $DocumentName; $pks = [string]$PeriodKey
    $a.Cells++; $a.Rows += $RowCount; $a.Docs[$dn] = 1; $a.Periods[$pks] = 1
    $ac.Docs[$dn] = 1; $ac.Periods[$pks] = 1
    # Зведення за групами звіту (по методології і речовині; лише кількості й відносні похибки).
    $bk = Get-LandParityBucket $ClassName $RelErr $ac.RelThreshold
    $subst = '-'
    if ($OutputLabel -match '^(?:tons|gsec)_([0-9]+)') { $subst = $Matches[1] }
    $bkey = "$MethodologyCode|$subst"
    if (-not $ac.Bucket.ContainsKey($bkey)) {
        $ac.Bucket[$bkey] = @{ Methodology = $MethodologyCode; Substance = $subst; Matched = 0; Within = 0; Mismatch = 0; EcrOnly = 0; OracleOnly = 0; ExplainedSkip = 0; Other = 0; Zero = 0
            Rels = New-Object 'System.Collections.Generic.List[double]' }
    }
    $b = $ac.Bucket[$bkey]
    switch ($bk.Group) {
        'MATCHED' { $b.Matched++; if ($bk.Within) { $b.Within++ }; $b.Rels.Add($RelErr) }
        'MISMATCH' { $b.Mismatch++; $b.Rels.Add($RelErr) }
        'ECR_ONLY' { $b.EcrOnly++ }
        'ORACLE_ONLY' { $b.OracleOnly++ }
        'EXPLAINED_SKIP' { $b.ExplainedSkip++ }
        default { $b.Other++ }
    }
    if ($bk.Zero -or $ClassName -like '*ECR_ONLY_ZERO') { $b.Zero++ }
    $isMatch = ($ClassName -eq 'MATCH' -or $ClassName -eq 'AGG_MATCH')
    if ($null -eq $a.Sample -and $ClassName -notmatch '^(AGG_)?MATCH') { $a.Sample = "$DocumentName/$PeriodKey/$TableCode/$RowKey" }
    if ($ClassName -match '^(AGG_)?(MATCH|MISMATCH|NEAR)') {
        if (-not $ac.Rels.ContainsKey($k)) { $ac.Rels[$k] = New-Object 'System.Collections.Generic.List[double]' }
        $ac.Rels[$k].Add($RelErr)
    }
    if (-not $isMatch) {
        $ac.Detail.Add(($DocumentName, $PeriodKey, $TableCode, $RowKey, $RowCount, $EfidValue, $MethodologyCode, $OutputLabel, $ClassName, $RelErr.ToString('R', [Globalization.CultureInfo]::InvariantCulture)) -join "`t")
    }
}

function Get-LandParityPhaseColumns {
    param([string[]]$Columns, [string]$TableCode, [hashtable]$GroupByMap)
    if ($GroupByMap.ContainsKey($TableCode)) { return @($GroupByMap[$TableCode]) }
    if ($GroupByMap.ContainsKey('*')) { return @($GroupByMap['*']) }
    $rx = '^Land_Type(Fuel|FuelOrStream|StorMater|StorMaterial|StoredMaterial|BurnFuel|PumpedLiquid)$'
    $c = @($Columns | Where-Object { $_ -match $rx } | Select-Object -First 1)
    return $c
}

function Invoke-LandParityCompare {
    <#
    .PARAMETER DocumentFile  full-set sample files: {doc, periods: {"2026-01": {<AF table>: {columns, rows:[{_efid, Land_NAE, ...}]}}}}
    .PARAMETER Option        Months (int[]), StorageAbs, TolRel, TolAbs, AggregateBy ('None'|'NaePhase'), GroupBy (hashtable)
    #>
    param([Parameter(Mandatory)][string[]]$DocumentFile, [Parameter(Mandatory)][hashtable]$StateObject, [Parameter(Mandatory)][hashtable]$Oracle,
        [Parameter(Mandatory)][hashtable]$Accumulator, [hashtable]$Option = @{}, [scriptblock]$Progress)
    $st = $StateObject; $ac = $Accumulator
    $months = @(); if ($Option.ContainsKey('Months')) { $months = @($Option.Months) }
    $stAbs = 5e-13; if ($Option.ContainsKey('StorageAbs')) { $stAbs = [double]$Option.StorageAbs }
    $tolRel = 1e-12; if ($Option.ContainsKey('TolRel')) { $tolRel = [double]$Option.TolRel }
    $tolAbs = 1e-9; if ($Option.ContainsKey('TolAbs')) { $tolAbs = [double]$Option.TolAbs }
    $aggMode = 'None'; if ($Option.ContainsKey('AggregateBy')) { $aggMode = [string]$Option.AggregateBy }
    $groupByOpt = @{}; if ($Option.ContainsKey('GroupBy')) { $groupByOpt = $Option.GroupBy }
    $fi = 0

    foreach ($file in $DocumentFile) {
        $fi++
        $d = ConvertFrom-LandParityJson ([System.IO.File]::ReadAllText($file))
        $docName = [string](Get-LandParityV $d 'doc')
        $n = [int]($docName -replace '^LAND-PROD-', '')
        $periods = Get-LandParityV $d 'periods'
        if ($null -eq $periods) { continue }
        foreach ($ym in @($periods.Keys)) {
            $mo = [int]([string]$ym).Substring(5, 2)
            if ($months.Count -gt 0 -and $months -notcontains $mo) { continue }
            $pk = [int](([string]$ym).Replace('-', ''))
            $tables = $periods[$ym]
            foreach ($pt in @($tables.Keys)) {
                if ($pt -notlike 'AF_Air_Land*') { continue }
                $tbl = Get-LandParityTableCode $pt
                if (-not $tbl) { continue }
                $tdat = $tables[$pt]
                $cols = @(Get-LandParityV $tdat 'columns' | ForEach-Object { [string]$_ })
                $srcRows = @(Get-LandParityV $tdat 'rows')
                $phaseCols = @(Get-LandParityPhaseColumns $cols $tbl $groupByOpt)
                $hasNae = $cols -contains 'Land_NAE'; $hasNo = $cols -contains 'Land_No'
                # -GroupBy names a column the table does not have: every row would fall into ONE group (AMBIGUOUS_KEY). Warn, never substitute.
                if ($hasNae -and ($groupByOpt.ContainsKey($tbl) -or $groupByOpt.ContainsKey('*'))) {
                    foreach ($gc in $phaseCols) {
                        if ($cols -contains $gc) { continue }
                        $wk = "$tbl|$gc"
                        if ($ac.WarnedKeys.ContainsKey($wk)) { continue }
                        $ac.WarnedKeys[$wk] = 1
                        $hint = @($cols | Where-Object { $_ -match '^Land_Type' -or $_ -eq 'Land_Streams' })
                        $msg = "GroupBy column '$gc' is missing in table $tbl (document $docName, period $pk): all rows share one phase group."
                        if ($hint.Count -gt 0) { $msg += " Candidate columns present (hint only, NOT substituted): $($hint -join ', ')." }
                        $ac.Warnings.Add($msg)
                        Write-Warning $msg
                    }
                }
                $tk = "$docName|$pk|$tbl"

                # ambiguity per (efid, NAE, phase): several DIFFERENT Land_No share the key
                $members = @{}; $i = 0
                $infos = New-Object System.Collections.ArrayList
                foreach ($r in $srcRows) {
                    $i++
                    $efid = [string](Get-LandParityV $r '_efid')
                    $nae = ''; $ph = ''; $gk = ''
                    if ($hasNae) {
                        $nae = [string](Get-LandParityV $r 'Land_NAE')
                        if ($phaseCols.Count -gt 0) {
                            $ph = [string](Get-LandParityV $r $phaseCols[0])
                            $gk = (@($phaseCols | ForEach-Object { [string](Get-LandParityV $r $_) })) -join '|'
                        }
                    }
                    $gkey = "$efid|$nae|$gk"
                    $ln = "#$i"; if ($hasNo) { $ln = [string](Get-LandParityV $r 'Land_No') }
                    if (-not $members.ContainsKey($gkey)) { $members[$gkey] = @{ Nos = @{}; Rows = New-Object System.Collections.ArrayList } }
                    $members[$gkey].Nos[$ln] = 1
                    # The stand's key format decides: table-qualified once the document has such rows, else legacy p<doc>-<seq>.
                    $rkFormat = 'Legacy'; if ($st.TableKeyDocs.ContainsKey($docName)) { $rkFormat = 'Table' }
                    $rk = New-LandRowKey -DocId $n -TableCode $tbl -Index $i -KeyFormat $rkFormat
                    [void]$members[$gkey].Rows.Add($rk)
                    [void]$infos.Add(@{ Row = $r; Efid = $efid; Nae = $nae; Ph = $ph; Key = $gkey; RowKey = $rk })
                }
                $rowByKey = @{}   # RowKey -> рядок джерела: гілку селектор бере з рядка КОЖНОГО члена групи
                foreach ($inf in $infos) { $rowByKey[$inf.RowKey] = $inf.Row }
                $groupDone = @{}
                # D-425: рядки груп, які легасі не рахує (статус не Running) - для класу ECR_NON_RUNNING замість ECR_ONLY.
                $nonRunningRows = @{}
                foreach ($inf in $infos) { if (Test-LandParityNonRunning $inf.Row) { $nonRunningRows[$inf.RowKey] = 1 } }

                foreach ($info in $infos) {
                    $nae = $info.Nae; $efid = $info.Efid; $rk = $info.RowKey; $ph = $info.Ph
                    if ($nae -eq '') { continue }
                    $mem = $members[$info.Key]
                    $amb = ($mem.Nos.Count -gt 1)
                    $ok = "$efid|$mo|$pt|$($nae.Trim())"
                    if (-not $Oracle.ContainsKey($ok)) { continue }      # no stored legacy result: nothing to compare
                    if ($amb -and $aggMode -eq 'NaePhase') {
                        if ($groupDone.ContainsKey($info.Key)) { continue }
                        $groupDone[$info.Key] = 1
                    }
                    $rs = @($Oracle[$ok])
                    if ($ph -ne '') {
                        $nr = @($rs | Where-Object { $_.phase.Trim() -eq $ph.Trim() })
                        if ($nr.Count -gt 0) { $rs = $nr }
                    }
                    $legacy = @{}
                    foreach ($lr in $rs) {
                        $lk = "$($lr.methodology)|$($lr.component)"
                        if (-not $legacy.ContainsKey($lk)) { $legacy[$lk] = New-Object System.Collections.ArrayList }
                        [void]$legacy[$lk].Add($lr)
                    }
                    $grouped = ($amb -and $aggMode -eq 'NaePhase')
                    if ($grouped) { $ac.AggregateGroups++ }
                    $rowKeys = @($rk)
                    if ($grouped) { $rowKeys = @($mem.Rows.ToArray()) }
                    $nRows = $rowKeys.Count
                    $pre = ''; if ($grouped) { $pre = 'AGG_' }
                    $stV = Get-LandParityV $info.Row 'Land_Status'

                    # loaded / ECR state of the members
                    $ecrRows = @(); $notLoaded = 0; $anyRow = $false
                    foreach ($mk in $rowKeys) {
                        if (-not $st.Loaded.Contains("$tk|$mk")) { $notLoaded++ }
                        $er = $null; if ($st.Ecr.ContainsKey("$tk|$mk")) { $er = $st.Ecr["$tk|$mk"] }
                        if ($null -ne $er) { $ecrRows += , $er; $anyRow = $true }
                    }
                    $notLoadedClass = 'ROW_NOT_LOADED_NO_CELLS'
                    $mxr = $null; if ($st.MaxRows.ContainsKey($tbl)) { $mxr = $st.MaxRows[$tbl] }
                    $cnt = 0; if ($st.LoadedCnt.ContainsKey($tk)) { $cnt = [int]$st.LoadedCnt[$tk] }
                    if ($mxr -and $cnt -ge $mxr) { $notLoadedClass = 'ROW_NOT_LOADED_CAPPED' }

                    $seen = @{}
                    foreach ($lk in $legacy.Keys) {
                        $meth, $comp = $lk -split '\|', 2
                        $copies = @($legacy[$lk] | Sort-Object { $_.modified })
                        $latest = $copies[-1]
                        foreach ($metric in 'tons', 'gsec') {
                            $outCode = "ECW_EC_${metric}_$comp"; $lab = "${metric}_$comp"
                            $oldV = $latest[$metric]
                            if ($amb -and -not $grouped) { Add-LandParityCell $ac $docName $pk $tbl $rk 1 $efid $meth $lab 'AMBIGUOUS_KEY'; continue }
                            if ($copies.Count -gt 1) {
                                $vals = @($copies | ForEach-Object { $_[$metric] } | Where-Object { $null -ne $_ } | Select-Object -Unique)
                                if ($vals.Count -gt 1) { Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab ($pre + 'LEGACY_DUP_CONFLICT'); continue }
                            }
                            if ($null -eq $oldV) { continue }
                            $ek = "$meth|$outCode"
                            # Селектор гілки: легасі пише одну гілку за NAE/потоком; порівнюється лише вона, решта -
                            # ECR_ONLY_NOT_SELECTED. Діє, лише якщо в ECR є значення з гілками для цього виходу.
                            $selPath = $false
                            if (@($st.BranchSelector) -contains $meth) {
                                foreach ($mk in $rowKeys) { if ($st.EcrVar.ContainsKey("$tk|$mk|$ek")) { $selPath = $true; break } }
                            }
                            $useRows = $ecrRows
                            if ($selPath) {
                                $useRows = @()
                                $notSel = @{}
                                foreach ($mk in $rowKeys) {
                                    $hv = @{}
                                    $mRow = $rowByKey[$mk]; if ($null -eq $mRow) { $mRow = $info.Row }
                                    $selBranch = Get-LandParitySelectedBranch -MethodologyCode $meth -MetricName $metric -Row $mRow
                                    $vv = $null; if ($st.EcrVar.ContainsKey("$tk|$mk|$ek")) { $vv = $st.EcrVar["$tk|$mk|$ek"] }
                                    if ($null -ne $vv) {
                                        foreach ($bn in @($vv.Keys)) {
                                            if ($null -ne $selBranch -and $bn -eq $selBranch) { $hv[$ek] = $vv[$bn] } else { $notSel[$bn] = 1 }
                                        }
                                    }
                                    $useRows += , $hv
                                }
                                $seen[$ek] = 1
                                foreach ($bn in $notSel.Keys) {
                                    $ac.BranchNotSelected++
                                    Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth "${lab}_$bn" ($pre + 'ECR_ONLY_NOT_SELECTED')
                                }
                            }
                            # Обидві гілки type1/type2 мають результат (після зняття гілки ключ один): старий код пише одну.
                            $bothHit = $false
                            if (-not $selPath) {
                                foreach ($mk in $rowKeys) { if ($st.BranchBoth.ContainsKey("$tk|$mk|$ek")) { $bothHit = $true; break } }
                            }
                            if ($bothHit) {
                                $seen[$ek] = 1
                                Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab ($pre + 'BRANCH_BOTH_PRESENT')
                                continue
                            }
                            # Legacy key shared by several tables with different values: not comparable (values never printed).
                            $ecrAmb = $false
                            foreach ($mk in $rowKeys) { if ($st.AmbiguousEcr.ContainsKey("$tk|$mk|$ek")) { $ecrAmb = $true; break } }
                            if ($ecrAmb) {
                                $seen[$ek] = 1
                                Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab ($pre + 'AMBIGUOUS_ECR_KEY')
                                continue
                            }
                            # ECR side: the row value, or the SUM over the group
                            $sum = 0.0; $have = 0; $diffCount = 0
                            foreach ($er in $useRows) {
                                if ($er.ContainsKey($ek)) { $sum += [double]$er[$ek]; $have++; $seen[$ek] = 1 }
                                else {
                                    foreach ($ck in $er.Keys) {
                                        if ($ck.EndsWith("|$outCode", [System.StringComparison]::Ordinal)) { $diffCount++; $seen[$ck] = 1; break }
                                    }
                                }
                            }
                            if ($have -eq 0) {
                                if ($diffCount -gt 0) { Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab ($pre + 'METHODOLOGY_DIFF'); continue }
                                if ($notLoaded -gt 0 -and -not $anyRow) { $cls = $pre + $notLoadedClass }
                                else {
                                    $tm = $st.EcrMeth.ContainsKey("$tk|$meth")
                                    if (-not $tm) { $cls = Get-LandParityMissingClass $st $meth }
                                    elseif (-not $anyRow) { $cls = 'MISSING_ROW' }
                                    else { $cls = 'MISSING_OUTPUT' }
                                    if ($cls -in 'MISSING_ROW', 'MISSING_OUTPUT' -and $null -ne $stV -and [string]$stV -ne '' -and [string]$stV -ne 'Running') { $cls = 'LEGACY_STATUS_SKIP' }
                                    $cls = $pre + $cls
                                }
                                if ([double]$oldV -eq 0) { $cls = $cls + '_ZERO' }
                                Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab $cls
                                continue
                            }
                            if ($selPath) { $ac.BranchSelected++ }
                            $scaled = $stAbs * [math]::Max(1, $have)
                            $vc = Get-LandParityValueClass ([double]$oldV) $sum $scaled $tolRel $tolAbs
                            $cls = $vc.Class
                            if ($grouped -and $cls -like 'MISMATCH*' -and $notLoaded -gt 0) { $cls = 'INCOMPLETE_' + $notLoadedClass }
                            Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab ($pre + $cls) $vc.Rel
                        }
                    }
                    # ECR outputs the legacy store does not have (aggregated over the group)
                    $onlySum = @{}
                    $onlyNotSel = @{}   # "meth|output" -> @{ гілка = 1 }: гілки, яких легасі не обирає (селектор гілки)
                    foreach ($mk in $rowKeys) {
                        $erKey = "$tk|$mk"
                        if (-not $st.Ecr.ContainsKey($erKey)) { continue }
                        $er = $st.Ecr[$erKey]
                        foreach ($ck in $er.Keys) {
                            if ($seen.ContainsKey($ck)) { continue }
                            $vv = $null; if ($st.EcrVar.ContainsKey("$tk|$mk|$ck")) { $vv = $st.EcrVar["$tk|$mk|$ck"] }
                            $ckMeth = ($ck -split '\|', 2)[0]
                            if ($null -ne $vv -and @($st.BranchSelector) -contains $ckMeth) {
                                $ckMetric = ''; if ($ck -match 'ECW_EC_(tons|gsec)_') { $ckMetric = $Matches[1] }
                                $mRow = $rowByKey[$mk]; if ($null -eq $mRow) { $mRow = $info.Row }
                                $sb = Get-LandParitySelectedBranch -MethodologyCode $ckMeth -MetricName $ckMetric -Row $mRow
                                foreach ($bn in @($vv.Keys)) {
                                    if ($null -ne $sb -and $bn -eq $sb) {
                                        if (-not $onlySum.ContainsKey($ck)) { $onlySum[$ck] = 0.0 }
                                        $onlySum[$ck] += [double]$vv[$bn]
                                    }
                                    else {
                                        if (-not $onlyNotSel.ContainsKey($ck)) { $onlyNotSel[$ck] = @{} }
                                        $onlyNotSel[$ck][$bn] = 1
                                    }
                                }
                                continue
                            }
                            if (-not $onlySum.ContainsKey($ck)) { $onlySum[$ck] = 0.0 }
                            $onlySum[$ck] += [double]$er[$ck]
                        }
                    }
                    foreach ($ck in $onlyNotSel.Keys) {
                        $outCodeNs = ($ck -split '\|', 2)[1]
                        $methNs = ($ck -split '\|', 2)[0]
                        foreach ($bn in $onlyNotSel[$ck].Keys) {
                            $ac.BranchNotSelected++
                            Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $methNs ((($outCodeNs -replace '^ECW_EC_', '') + '_' + $bn)) ($pre + 'ECR_ONLY_NOT_SELECTED')
                        }
                    }
                    foreach ($ck in $onlySum.Keys) {
                        $meth, $outCode = $ck -split '\|', 2
                        $lab = $outCode -replace '^ECW_EC_', ''
                        if ($lab -match '_NOx$|SumXiDiv') { $cls = 'ECR_ONLY_INTERMEDIATE' } elseif ($onlySum[$ck] -eq 0) { $cls = 'ECR_ONLY_ZERO' } else { $cls = 'ECR_ONLY' }
                        if ($amb -and -not $grouped) { $cls = 'AMBIGUOUS_KEY' }
                        foreach ($mk in $rowKeys) { if ($st.AmbiguousEcr.ContainsKey("$tk|$mk|$ck")) { $cls = 'AMBIGUOUS_ECR_KEY'; break } }
                        # D-425: ECR порахував рядок, який легасі не рахує (статус не Running) - це регресія фільтра
                        # «лише Running», а не «ECR порахував там, де легасі не було»; індикатор має бути 0.
                        if ($cls -like 'ECR_ONLY*') {
                            # Клас - лише коли на не-Running рядку Є результат ECR (не за самою наявністю не-Running члена групи).
            foreach ($mk in $rowKeys) {
                if ($nonRunningRows.ContainsKey($mk) -and $st.Ecr.ContainsKey("$tk|$mk") -and $st.Ecr["$tk|$mk"].ContainsKey($ck)) { $cls = 'ECR_NON_RUNNING'; break }
            }
                        }
                        Add-LandParityCell $ac $docName $pk $tbl $rk $nRows $efid $meth $lab ($pre + $cls)
                    }
                }
            }
        }
        if ($Progress) { & $Progress $fi $DocumentFile.Count }
    }
}

function Get-LandParityPercentile {
    param($Values, [double]$Fraction)
    if ($Values.Count -eq 0) { return $null }
    $s = $Values.ToArray(); [Array]::Sort($s)
    return $s[[int][math]::Min($s.Length - 1, [math]::Floor($Fraction * ($s.Length - 1)))]
}

function Get-LandParitySummary {
    # Identifiers, counters, classes and relative errors only.
    param([Parameter(Mandatory)][hashtable]$Accumulator, [hashtable]$StateObject)
    $ac = $Accumulator
    $groups = @(foreach ($k in $ac.Agg.Keys) {
            $a = $ac.Agg[$k]; $l = $null
            if ($ac.Rels.ContainsKey($k)) { $l = $ac.Rels[$k] }
            [ordered]@{
                methodology = $a.Methodology; table = $a.Table; class = $a.Class; cells = [int]$a.Cells; rows = [int]$a.Rows
                docs = [int]$a.Docs.Count; periods = (@($a.Periods.Keys | Sort-Object) -join ','); sample = $a.Sample
                relMedian = $(if ($l) { Get-LandParityPercentile $l 0.5 } else { $null })
                relP95 = $(if ($l) { Get-LandParityPercentile $l 0.95 } else { $null })
                relMax = $(if ($l) { Get-LandParityPercentile $l 1.0 } else { $null })
            }
        })
    $groups = @($groups | Sort-Object { $_.class }, { $_.methodology }, { $_.table })
    $byClass = @{}; $byMeth = @{}
    foreach ($g in $groups) {
        if (-not $byClass.ContainsKey($g.class)) { $byClass[$g.class] = 0 }
        $byClass[$g.class] += $g.cells
        if (-not $byMeth.ContainsKey($g.methodology)) { $byMeth[$g.methodology] = @{} }
        if (-not $byMeth[$g.methodology].ContainsKey($g.class)) { $byMeth[$g.methodology][$g.class] = 0 }
        $byMeth[$g.methodology][$g.class] += $g.cells
    }
    $classes = @($byClass.Keys | Sort-Object { - $byClass[$_] } | ForEach-Object { [ordered]@{ class = $_; cells = [int]$byClass[$_] } })
    $byMethodology = @($byMeth.Keys | Sort-Object | ForEach-Object {
            $mc = $_
            $cl = @($byMeth[$mc].Keys | Sort-Object { - $byMeth[$mc][$_] } | ForEach-Object { [ordered]@{ class = $_; cells = [int]$byMeth[$mc][$_] } })
            [ordered]@{ methodology = $mc; cells = [int](($cl | ForEach-Object { $_.cells } | Measure-Object -Sum).Sum); classes = $cl }
        })
    $multi = @()
    if ($null -ne $StateObject) { $multi = @($StateObject.MultiTable.Keys | Sort-Object | ForEach-Object { [ordered]@{ methodology = $_; results = [int]$StateObject.MultiTable[$_] } }) }
    $ambEcr = [ordered]@{ keys = 0; byMethodology = @() }; $keyFmt = [ordered]@{ table = 0; legacy = 0; unknown = 0 }
    if ($null -ne $StateObject) {
        $ambEcr = [ordered]@{ keys = [int]$StateObject.AmbiguousEcr.Count; byMethodology = @($StateObject.AmbiguousByMeth.Keys | Sort-Object | ForEach-Object { [ordered]@{ methodology = $_; keys = [int]$StateObject.AmbiguousByMeth[$_] } }) }
        $keyFmt = [ordered]@{ table = [int]$StateObject.KeyFormats['Table']; legacy = [int]$StateObject.KeyFormats['Legacy']; unknown = [int]$StateObject.KeyFormats['Unknown'] }
    }
    $withTi = 0; $withoutTi = 0
    if ($null -ne $StateObject) { $withTi = [int]$StateObject.ResultsWithTableInstance; $withoutTi = [int]$StateObject.ResultsWithoutTableInstance }
    $noSub = @(); $foreign = @()
    if ($null -ne $StateObject) {
        $noSub = @($StateObject.NoSubstance.Keys | Sort-Object | ForEach-Object { [ordered]@{ methodology = $_; results = [int]$StateObject.NoSubstance[$_] } })
        $foreign = @($StateObject.ForeignSubstance.Keys | Sort-Object | ForEach-Object { [ordered]@{ methodology = $_; results = [int]$StateObject.ForeignSubstance[$_] } })
    }
    $bySubstance = @(foreach ($bk in $ac.Bucket.Keys) {
            $b = $ac.Bucket[$bk]
            [ordered]@{
                methodology = $b.Methodology; substance = $b.Substance; matched = [int]$b.Matched; withinThreshold = [int]$b.Within
                mismatch = [int]$b.Mismatch; ecrOnly = [int]$b.EcrOnly; oracleOnly = [int]$b.OracleOnly; explainedSkip = [int]$b.ExplainedSkip; other = [int]$b.Other; zero = [int]$b.Zero
                relP95 = $(if ($b.Rels.Count -gt 0) { Get-LandParityPercentile $b.Rels 0.95 } else { $null })
                relMax = $(if ($b.Rels.Count -gt 0) { Get-LandParityPercentile $b.Rels 1.0 } else { $null })
            }
        })
    $bySubstance = @($bySubstance | Sort-Object { $_.methodology }, { $_.substance })
    $buckets = @(foreach ($mn in @($bySubstance | ForEach-Object { $_.methodology } | Sort-Object -Unique)) {
            $rowsOfM = @($bySubstance | Where-Object { $_.methodology -eq $mn })
            $relMaxM = $null
            foreach ($x in $rowsOfM) { if ($null -ne $x.relMax -and ($null -eq $relMaxM -or $x.relMax -gt $relMaxM)) { $relMaxM = $x.relMax } }
            [ordered]@{
                methodology = $mn
                matched = [int](($rowsOfM | ForEach-Object { $_.matched } | Measure-Object -Sum).Sum)
                withinThreshold = [int](($rowsOfM | ForEach-Object { $_.withinThreshold } | Measure-Object -Sum).Sum)
                mismatch = [int](($rowsOfM | ForEach-Object { $_.mismatch } | Measure-Object -Sum).Sum)
                ecrOnly = [int](($rowsOfM | ForEach-Object { $_.ecrOnly } | Measure-Object -Sum).Sum)
                oracleOnly = [int](($rowsOfM | ForEach-Object { $_.oracleOnly } | Measure-Object -Sum).Sum)
                explainedSkip = [int](($rowsOfM | ForEach-Object { $_.explainedSkip } | Measure-Object -Sum).Sum)
                other = [int](($rowsOfM | ForEach-Object { $_.other } | Measure-Object -Sum).Sum)
                zero = [int](($rowsOfM | ForEach-Object { $_.zero } | Measure-Object -Sum).Sum)
                relMax = $relMaxM
            }
        })
    return [ordered]@{
        relThreshold = $ac.RelThreshold
        buckets = $buckets
        bySubstance = $bySubstance
        outputsWithoutSubstance = $noSub
        foreignSubstanceIgnored = $foreign
        totalCells = [int](($classes | ForEach-Object { $_.cells } | Measure-Object -Sum).Sum)
        # D-425: індикатор регресії фільтра «лише Running»: ECR порахував рядок, якого легасі не рахує. Має бути 0.
        ecrNonRunningCells = [int](($classes | Where-Object { $_.class -like '*ECR_NON_RUNNING' } | ForEach-Object { $_.cells } | Measure-Object -Sum).Sum)
        # Селектор гілки: клітинки, порівняні за гілкою легасі, і клітинки ECR_ONLY_NOT_SELECTED (решта гілок ECR).
        branchSelectedCells = [int]$ac.BranchSelected
        notSelectedCells = [int]$ac.BranchNotSelected
        classes = $classes
        byMethodology = $byMethodology
        multiTableResults = $multi
        ambiguousEcrKeys = $ambEcr
        rowKeyFormats = $keyFmt
        resultsWithTableInstance = $withTi
        resultsWithoutTableInstance = $withoutTi
        missingBindingInactiveCells = [int]($classes | Where-Object { $_.class -like '*MISSING_BINDING_INACTIVE*' } | ForEach-Object { $_.cells } | Measure-Object -Sum).Sum
        groups = $groups
        warnings = @($ac.Warnings.ToArray())
    }
}
