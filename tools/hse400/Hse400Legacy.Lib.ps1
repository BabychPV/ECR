<#
    HSE400 на реальних даних (етап 8): зіставлення колонок старої системи (AF_AirGasCompositions_HSE400 і
    CalcResult_AirGasCompositions_HSE400) з рядками шаблону HSE400 і звірка комірок документа з результатом старої системи.
    Чиста логіка без мережі й без SQL; тести - tools/tests/Hse400-Legacy.Tests.ps1 (синтетичні дані).

    Договір імен (перевірено на копії прода, лише імена колонок, без значень):
      - вхід: колонка AF_AirGasCompositions_HSE400 = <код таблиці>_<ключ рядка вводу> (FG_Makat_Methane -> таблиця FG_Makat,
        рядок Methane); усі 175 рядків вводу шаблону мають однойменну колонку; зайві колонки AF: EFID, ModifiedTime, Time і
        <FG|LPG>_HMB_TotalSumGasComposition (сума, у шаблоні це розрахунковий рядок, а не ввід);
      - результат: колонка CalcResult_... = <код таблиці>_<ім'я формули без HSE400_<таблиця>_> (рядок шаблону F_<X>), з винятками:
          * формула названа як рядок вводу (Nitrogen у FG_Makat/FG_Export; H2S, COS, MMercaptan, EMercaptan, DimethilSulfide,
            nPMercaptan, nBMercaptan, Nitrogen у *_Internal) - колонка вводу вже зайнята, результат лежить у <X>V2 (Makat, Export)
            або <X>_V2 (Internal);
          * Density20/Density0/FuelLowerHeating20/FuelLowerHeating0 у FG_Makat - колонки Density_20 тощо;
          * HMB: MercaptanSulphurContent зберігається як TotalSulphurContentWithoutH2S (Q_Air_HSE400.cs:127/:217 за
            tests/Ecr.Architecture.Tests/LandHse400CompositionScriptTests.cs);
          * F_GHG1/F_GHG2 - проміжні кроки коефіцієнта викидів: у старій системі їх немає.
      Зіставлення рішається за фактичним набором колонок (передається в -Columns), тож відсутня пара - це явна
      «немає пари», а не мовчазна розбіжність.
#>

Set-StrictMode -Version 2.0

$script:Hse400LegacyIntermediateRows = @('F_GHG1', 'F_GHG2')

function Get-Hse400LegacyResultColumn {
    <#
    Колонка CalcResult для розрахункового рядка шаблону. -InputKeys - ключі вводу тієї ж таблиці, -Columns - усі колонки
    CalcResult. Повертає ім'я колонки або $null (немає пари).
    #>
    param([Parameter(Mandatory)][string]$Table, [Parameter(Mandatory)][string]$RowKey,
        [Parameter(Mandatory)][string[]]$InputKeys, [Parameter(Mandatory)][string[]]$Columns)
    if ($RowKey -notlike 'F_*') { return $null }
    if ($script:Hse400LegacyIntermediateRows -contains $RowKey) { return $null }
    $x = $RowKey.Substring(2)
    $have = @{}; foreach ($c in $Columns) { $have[$c] = $true }
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($InputKeys -contains $x) {
        $candidates.Add("${Table}_${x}V2"); $candidates.Add("${Table}_${x}_V2")
    }
    else {
        $candidates.Add("${Table}_${x}")
        if ($x -match '^(Density|FuelLowerHeating)(\d+)$') { $candidates.Add("${Table}_$($Matches[1])_$($Matches[2])") }
        if ($x -eq 'MercaptanSulphurContent' -and $Table -like '*_HMB') { $candidates.Add("${Table}_TotalSulphurContentWithoutH2S") }
    }
    foreach ($c in $candidates) { if ($have.ContainsKey($c)) { return $c } }
    return $null
}

function Get-Hse400LegacyInputColumn {
    <# Колонка AF для рядка вводу шаблону (<таблиця>_<ключ>) або $null, якщо такої колонки немає. #>
    param([Parameter(Mandatory)][string]$Table, [Parameter(Mandatory)][string]$RowKey, [Parameter(Mandatory)][string[]]$Columns)
    $n = "${Table}_${RowKey}"
    if ($Columns -contains $n) { return $n }
    return $null
}

function Join-Hse400LegacyItems {
    <#
    Модель + вибірка -> елементи для Get-Hse400YearInputRows / Compare-Hse400YearResults (Hse400Year.Lib.ps1).
    -Months: @{ <номер місяця> = @{ inputs = @{колонка = число}; results = @{колонка = число} } }.
    Повертає @{ Items; NoPair; UnusedInputColumns; UnusedResultColumns }:
      Items - @{ Table; RowKey; Kind; SourceRow(=колонка старої системи); Values(12) }; NoPair - рядки без колонки старої системи.
    #>
    param([Parameter(Mandatory)]$Model, [Parameter(Mandatory)][hashtable]$Months,
        [Parameter(Mandatory)][string[]]$InputColumns, [Parameter(Mandatory)][string[]]$ResultColumns)
    $items = New-Object System.Collections.Generic.List[object]
    $noPair = New-Object System.Collections.Generic.List[string]
    $usedIn = @{}; $usedOut = @{}
    foreach ($t in @($Model.tables)) {
        $inputKeys = @(@($t.rows) | Where-Object { [string]$_.kind -eq 'Input' } | ForEach-Object { [string]$_.key })
        foreach ($r in @($t.rows)) {
            $key = [string]$r.key; $kind = [string]$r.kind
            if ($kind -eq 'Input') { $col = Get-Hse400LegacyInputColumn -Table $t.code -RowKey $key -Columns $InputColumns; $src = 'inputs'; $usedIn[[string]$col] = $true }
            else { $col = Get-Hse400LegacyResultColumn -Table $t.code -RowKey $key -InputKeys $inputKeys -Columns $ResultColumns; $src = 'results'; if ($col) { $usedOut[$col] = $true } }
            if (-not $col) { $noPair.Add("$($t.code)/$key"); continue }
            $vals = New-Object object[] 12
            for ($m = 1; $m -le 12; $m++) {
                if ($Months.ContainsKey($m) -and $Months[$m][$src].ContainsKey($col)) { $vals[$m - 1] = $Months[$m][$src][$col] }
            }
            $items.Add([pscustomobject]@{ Table = [string]$t.code; RowKey = $key; Kind = $kind; SourceRow = $col; Values = $vals })
        }
    }
    $uIn = @($InputColumns | Where-Object { -not $usedIn.ContainsKey($_) })
    $uOut = @($ResultColumns | Where-Object { -not $usedOut.ContainsKey($_) })
    return [pscustomobject]@{ Items = $items.ToArray(); NoPair = $noPair.ToArray(); UnusedInputColumns = $uIn; UnusedResultColumns = $uOut }
}

function Get-Hse400LegacyMonthIndex {
    <# Номер місяця (1..12) для мітки часу рядка старої системи в заданому році; чужий рік - $null. #>
    param([Parameter(Mandatory)][datetime]$Time, [Parameter(Mandatory)][int]$Year)
    if ($Time.Year -ne $Year) { return $null }
    return $Time.Month
}

function Get-Hse400LegacyDiffClass {
    <#
    Клас розбіжності комірки (для звіту): для кожної розбіжної пари - що саме не збіглося.
      OnlyNew   - у старої системи значення немає (null), у нової є;  OnlyOld - навпаки;
      Rounding  - різниця в межах точності збереження старої системи (абсолютна ≤ AbsStore, відносна > RelTol);
      Value     - числа різні.
    #>
    param($Expected, $Actual, [double]$RelTol = 1e-9, [double]$AbsStore = 1e-9)
    if ($null -eq $Expected -and $null -ne $Actual) { return 'OnlyNew' }
    if ($null -ne $Expected -and $null -eq $Actual) { return 'OnlyOld' }
    $d = [Math]::Abs([double]$Expected - [double]$Actual)
    if ($d -le $AbsStore) { return 'Rounding' }
    return 'Value'
}
