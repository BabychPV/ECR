# tools/land/LandNaeType.Lib.ps1
# D-423 (C11_10): вибір формули type1/type2/type3 за NAE, як у старому коді. Чиста логіка без звернень до API; значень
# прода тут немає.
#
# Старий код ECW_C11_10.cs: тип формули визначається ЛИШЕ за NAE рядка (typeCalculationGroup :180-184, цикл :305-311) і
# рахується тільки обраний тип (`formulasString` :328); NAE поза групами - помилка «formula's type» і `continue` (:313-326),
# результату немає. NAE нормалізується: пробіли прибираються, хвіст від `(` відрізається (:133-141).
# В ECR усі три типи - виходи (`ECW_EC_(tons|gsec)_XXX_type1|2|3`), тож для кожного рядка виходять усі, а в T9_02/T9_11
# (прод-джерело: лише NAE type1, ключа OperatingTime немає) type2/type3 рахувалися на порожньому `@Land_Measure_OperatingTime?`
# і давали ненульові константи там, де легасі рахує лише type1 (2 834 клітинок MISMATCH_LEGACY_ZERO).
#
# Правка за зразком ParityBranch: вираз кожного типу обгортається
#   `if(in(@Land_NAE, '<NAE типу>', ...), <вираз>, NULL)`
# - тип, не обраний за NAE, не дає числа (вихід без числа не пишеться, GenericCalculationModule.Emit). Словник виразу не має
# рядкових функцій, тому шість NAE з хвостом (`0647 (001-002)` .. `0652 (001-002)`; решта хвостів у даних NAE поза групами)
# перелічено обома написаннями - так само, як у правилах категорії. Рушій, контракт, міграція не змінюються.

$script:LandNaeTypeGroups = [ordered]@{
    'ECW_C11_10' = [ordered]@{
        'type1' = @('0580', '0581', '0583', '0584', '0586', '0587', '0588', '0646', '0960', '0961', '0962', '0963', '0964', '0965', '0966', '0968', '0970',
            '0340', '0341', '0342', '0343', '0344', '0345', '0346', '0347', '0362', '0363', '0364', '0365', '0366', '0367', '0368', '0369', '0589')
        'type2' = @('0647', '0648', '0649', '0650', '0651', '0652', '0884', '0885', '0886', '0887', '0888', '0889', '0890', '0891', '0892', '0893', '0894', '0895')
        'type3' = @('0220', '0221', '0222', '0280', '0281')
    }
}

# NAE, що в прод-даних пишуться з хвостом групи `(001-002)`; для них у списку додається друге написання.
$script:LandNaeTypeTailed = @('0647', '0648', '0649', '0650', '0651', '0652')

# D-435 (C11_13): вихід дозволений за NAE рядка, а не за типом формули. Легасі ECW_C11_13.cs:66-133: вихід
# `ECW_EC_tons_<k>_<type>` + `ECW_EC_gsec_<k>` (gsec БЕЗ суфікса type) пишеться, коли category = Land_NAE.Trim().Replace(" ","")
# одночасно в EC[<k>] і в catTons[<type>] (для gsec - у EC[<k>], бо кожен NAE з EC входить в один із catTons).
# Нормалізація - ЛИШЕ Trim + без пробілів, хвіст `(003)` НЕ відрізається (на відміну від C11_10): NAE `0500(003)` лишається
# ключем, а в `in()` перелічуються обидва написання `0500(003)` і `0500 (003)`. Порожній перелік (легасі такий вихід не пише
# ніколи) -> вираз `NULL`.
$script:LandNaeC1113Ec = [ordered]@{
    '333'  = @('6670', '6672', '6674')
    '1078' = @('6568', '6574', '6575', '6567', '6669', '6671', '6673')
    '2754' = @('6670', '6672', '6674')
    '2735' = @('0500(003)', '0501(003)', '0502(003)', '0503(003)', '0520(003)', '0521(003)', '0522(003)', '0523(003)')
}
$script:LandNaeC1113CatTons = [ordered]@{
    'type1' = @('6567', '6568', '6574', '6575', '0500(003)', '0501(003)', '0502(003)', '0503(003)', '0520(003)', '0521(003)', '0522(003)', '0523(003)')
    'type2' = @('6669', '6671', '6673', '6670', '6672', '6674')
}

function Get-LandNaeOutputGroups {
    <# «методологія -> {код формули-виходу -> NAE[] (нормалізовані, як у легасі)}»: EC[k] перетин catTons[type] для tons, EC[k] для gsec. #>
    $g = [ordered]@{}
    $allTons = @($script:LandNaeC1113CatTons.Values | ForEach-Object { $_ })
    foreach ($k in $script:LandNaeC1113Ec.Keys) {
        foreach ($t in $script:LandNaeC1113CatTons.Keys) {
            $g["ECW_EC_tons_${k}_$t"] = @($script:LandNaeC1113Ec[$k] | Where-Object { $script:LandNaeC1113CatTons[$t] -ccontains $_ })
        }
        $g["ECW_EC_gsec_$k"] = @($script:LandNaeC1113Ec[$k] | Where-Object { $allTons -ccontains $_ })
    }
    return [ordered]@{ 'ECW_C11_13' = $g }
}

function ConvertTo-LandNaeC1113Normalized {
    <# Нормалізація NAE як у ECW_C11_13.cs:66: Trim + без пробілів; хвіст `(003)` лишається частиною ключа. #>
    param([AllowNull()][AllowEmptyString()][string]$Nae)
    if ([string]::IsNullOrEmpty($Nae)) { return '' }
    return $Nae.Trim().Replace(' ', '')
}

function Get-LandNaeOutputList {
    <# NAE з обома написаннями для кодів із хвостом `(...)`: `0500(003)` і `0500 (003)`; порядок стабільний. #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Naes)
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($n in $Naes) {
        $out.Add($n)
        $ix = $n.IndexOf('(')
        if ($ix -gt 0 -and $n[$ix - 1] -ne ' ') { $out.Add($n.Substring(0, $ix) + ' ' + $n.Substring($ix)) }
    }
    return $out.ToArray()
}

function Convert-LandNaeOutputFormula {
    <#
    .SYNOPSIS
        C11_13: обгортка виходу за переліком NAE (EC перетин type). Порожній перелік -> вираз `NULL` (аргументи не чіпаються).
        Непорожній - як Convert-LandNaeTypeFormula, але з обома написаннями `(003)`. Повтор - Changed = $false.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expression,
        [AllowNull()][AllowEmptyString()]$ArgumentsCsv = $null,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Naes
    )
    $text = $Expression.Trim()
    if ($text.Length -eq 0) {
        return [pscustomobject]@{ Changed = $false; Expression = $Expression; ArgumentsCsv = $ArgumentsCsv }
    }
    if (@($Naes).Count -eq 0) {
        $same = ($text -ceq 'NULL')
        return [pscustomobject]@{ Changed = (-not $same); Expression = $(if ($same) { $Expression } else { 'NULL' }); ArgumentsCsv = $ArgumentsCsv }
    }
    $items = (Get-LandNaeOutputList -Naes $Naes | ForEach-Object { "'" + $_ + "'" }) -join ', '
    $head = "if(in(@Land_NAE, $items), "
    $wrapped = $text.StartsWith($head, [StringComparison]::Ordinal) -and $text.EndsWith(', NULL)', [StringComparison]::Ordinal)
    $newExpression = if ($wrapped) { $Expression } else { $head + $text + ', NULL)' }
    $newArguments = $ArgumentsCsv
    $argsChanged = $false
    if ($null -ne $ArgumentsCsv) {
        $tokens = New-Object System.Collections.Generic.List[string]
        $present = $false
        foreach ($t in (([string]$ArgumentsCsv) -split ';')) {
            $x = $t.Trim()
            if ($x.Length -eq 0) { continue }
            if (($x.TrimStart('@').TrimEnd('?').TrimEnd()) -ieq 'Land_NAE') { $present = $true }
            $tokens.Add($x)
        }
        if (-not $present) { $tokens.Add('@Land_NAE'); $argsChanged = $true; $newArguments = (($tokens -join ';') + ';') }
    }
    return [pscustomobject]@{ Changed = ((-not $wrapped) -or $argsChanged); Expression = $newExpression; ArgumentsCsv = $newArguments }
}

function Get-LandNaeTypeGroups {
    <# Словник «методологія -> {type -> NAE[]}» (копія, щоб виклик не міняв стан модуля). #>
    $copy = [ordered]@{}
    foreach ($m in $script:LandNaeTypeGroups.Keys) {
        $one = [ordered]@{}
        foreach ($t in $script:LandNaeTypeGroups[$m].Keys) { $one[$t] = @($script:LandNaeTypeGroups[$m][$t]) }
        $copy[$m] = $one
    }
    return $copy
}

function Get-LandNaeTypeOfFormula {
    <# `type1|type2|type3`, якщо код формули - вихід цього типу (`..._XXX_type2`, `..._333_type2`); інакше $null. #>
    param([Parameter(Mandatory)][string]$FormulaCode)

    $m = [regex]::Match($FormulaCode, '^ECW_EC_(?:tons|gsec)_[A-Za-z0-9]+_(type[123])$')
    if (-not $m.Success) { return $null }
    return $m.Groups[1].Value
}

function Get-LandNaeTypeList {
    <# NAE типу з обома написаннями для кодів із хвостом `(001-002)`; порядок стабільний. #>
    param([Parameter(Mandatory)][string[]]$Naes)

    $out = New-Object System.Collections.Generic.List[string]
    foreach ($n in $Naes) {
        $out.Add($n)
        if ($script:LandNaeTypeTailed -contains $n) { $out.Add($n + ' (001-002)') }
    }
    return $out.ToArray()
}

function Get-LandNaeTypeHead {
    param([Parameter(Mandatory)][string[]]$Naes)
    $items = (Get-LandNaeTypeList -Naes $Naes | ForEach-Object { "'" + $_ + "'" }) -join ', '
    return "if(in(@Land_NAE, $items), "
}

function Test-LandNaeTypeWrapped {
    <# TRUE, якщо вираз уже обгорнуто (повтор - 0 змін). Голова перевіряється точно: інший перелік NAE = «не обгорнуто». #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expression,
        [Parameter(Mandatory)][string[]]$Naes
    )
    $text = $Expression.Trim()
    $head = Get-LandNaeTypeHead -Naes $Naes
    return $text.StartsWith($head, [StringComparison]::Ordinal) -and $text.EndsWith(', NULL)', [StringComparison]::Ordinal)
}

function Convert-LandNaeTypeFormula {
    <#
    .SYNOPSIS
        Обгортає вираз типу: порожній результат, якщо NAE рядка не належить групі типу; аргумент `@Land_NAE` додається
        в список (не необов'язковий: старий код без Land_NAE рядок не рахує, :43).
    .DESCRIPTION
        Порожній вираз не чіпається. Список $null лишається $null (формула без списку не перевіряється); порожній рядок
        «оголошує нуль аргументів», тому аргумент додається. Повтор - Changed = $false.
    .OUTPUTS
        pscustomobject: Changed, Expression, ArgumentsCsv.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expression,
        [AllowNull()][AllowEmptyString()]$ArgumentsCsv = $null,
        [Parameter(Mandatory)][string[]]$Naes
    )

    $text = $Expression.Trim()
    if ($text.Length -eq 0) {
        return [pscustomobject]@{ Changed = $false; Expression = $Expression; ArgumentsCsv = $ArgumentsCsv }
    }

    $wrappedAlready = Test-LandNaeTypeWrapped -Expression $text -Naes $Naes
    $newExpression = if ($wrappedAlready) { $Expression } else { (Get-LandNaeTypeHead -Naes $Naes) + $text + ', NULL)' }

    $newArguments = $ArgumentsCsv
    $argsChanged = $false
    if ($null -ne $ArgumentsCsv) {
        $tokens = New-Object System.Collections.Generic.List[string]
        $present = $false
        foreach ($t in (([string]$ArgumentsCsv) -split ';')) {
            $x = $t.Trim()
            if ($x.Length -eq 0) { continue }
            $core = $x.TrimStart('@').TrimEnd('?').TrimEnd()
            if ($core -ieq 'Land_NAE') { $present = $true }
            $tokens.Add($x)
        }
        if (-not $present) { $tokens.Add('@Land_NAE'); $argsChanged = $true }
        if ($argsChanged) { $newArguments = (($tokens -join ';') + ';') }
    }

    return [pscustomobject]@{
        Changed      = ((-not $wrappedAlready) -or $argsChanged)
        Expression   = $newExpression
        ArgumentsCsv = $newArguments
    }
}

function ConvertTo-LandNaeNormalized {
    <# Нормалізація NAE як у старому коді (ECW_C11_10.cs:133-141): Trim, без пробілів, хвіст від `(` відрізається. #>
    param([AllowNull()][AllowEmptyString()][string]$Nae)

    if ([string]::IsNullOrEmpty($Nae)) { return '' }
    $x = $Nae.Trim().Replace(' ', '')
    $ix = $x.IndexOf('(')
    if ($ix -ge 0) { $x = $x.Substring(0, $ix).Trim() }
    return $x
}

function Test-LandNaeInType {
    <# TRUE, якщо нормалізований NAE належить групі типу методології (за словником Get-LandNaeTypeGroups). #>
    param(
        [AllowNull()][AllowEmptyString()][string]$Nae,
        [Parameter(Mandatory)][string]$Methodology,
        [Parameter(Mandatory)][ValidateSet('type1', 'type2', 'type3')][string]$Type
    )

    if (-not $script:LandNaeTypeGroups.Contains($Methodology)) { return $false }
    $norm = ConvertTo-LandNaeNormalized -Nae $Nae
    return (@($script:LandNaeTypeGroups[$Methodology][$Type]) -contains $norm)
}