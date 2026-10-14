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