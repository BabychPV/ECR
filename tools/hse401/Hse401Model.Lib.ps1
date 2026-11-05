# tools/hse401/Hse401Model.Lib.ps1
# Модель шаблону HSE401: колонки (код, колонка Excel, тип, формула), групи заголовків, порядок.
# Спільна для Build-Hse401Template.ps1, Load-Hse401Document.ps1, Compare-Hse401.ps1, Test-Hse401FormulaEdit.ps1.

Set-StrictMode -Version 2.0

$script:HseMonthTon = 'U', 'W', 'Y', 'AC', 'AE', 'AG', 'AK', 'AM', 'AO', 'AS', 'AU', 'AW'
$script:HseMonthWd = 'V', 'X', 'Z', 'AD', 'AF', 'AH', 'AL', 'AN', 'AP', 'AT', 'AV', 'AX'
$script:HseQtrGs = 'AA', 'AI', 'AQ', 'AY'
$script:HseQtrTon = 'AB', 'AJ', 'AR', 'AZ'
$script:HseRawQ = @(@('BE', 'BF'), @('BG', 'BH'), @('BI', 'BJ'), @('BK', 'BL'))   # [т, годин] по кварталах
$script:HseMonthEn = 'January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'
$script:HseMonthRu = 'Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь', 'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь'

# Формат показу = рядок формату Excel як є (ColumnDef.DisplayFormat, <= 50 знаків, без валідації грамлики; ExcelExporter ставить його в NumberFormat).
$script:HseFmtLimit = '0.000000000000'   # R/S: 12 знаків
$script:HseFmtRaw = '0.0000'             # BC/BD: 4 знаки

$script:HseTemplateCode = 'HSE401'
$script:HseSheetCode = 'HSE'
$script:HseTableCode = 'HSE401_TABLE'

function New-HseCol {
    param([string]$Code, [string]$Xl, [string]$Type, [string]$En, [string]$Ru, [string]$Group, [string]$Registry = $null, [string]$Filter = $null,
        [string]$Expr = $null, [bool]$Hidden = $false, $Precision = $null, $Scale = $null, [string]$Kind = 'Input', [int]$Width = 0, [string]$DisplayFormat = $null)
    return [pscustomobject]@{ Code = $Code; Xl = $Xl; Type = $Type; En = $En; Ru = $Ru; Group = $Group; Registry = $Registry; Filter = $Filter
        Expr = $Expr; Hidden = $Hidden; Precision = $Precision; Scale = $Scale; Kind = $Kind; Width = $Width; DisplayFormat = $DisplayFormat }
}

function Get-Hse401Columns {
    <# Колонки в порядку шаблону. Kind: Input (вводиться), Formula (рахується), NewInput (нова вхідна, якої нема в Excel). #>
    param([string]$RateExpr = '[H_LIMIT_T] / IF([H_BASIS] = NULL, [Period].Days, [H_BASIS])')
    $c = New-Object System.Collections.Generic.List[object]
    $c.Add((New-HseCol H_NO B Int 'No' 'No' G_IDENT -Width 60))
    $c.Add((New-HseCol H_REGION C Lookup 'Region' 'Регион' G_IDENT -Registry HSE401_REGION))
    $c.Add((New-HseCol H_INFO D String 'Additional information' 'Дополнительная информация' G_IDENT -Width 200))
    $c.Add((New-HseCol H_ONOFF E Lookup 'On/Offshore' 'Суша/Море' G_IDENT -Registry HSE401_ONOFF))
    $c.Add((New-HseCol H_AREA F Lookup 'Area' 'Район работ' G_IDENT -Registry HSE401_AREA))
    $c.Add((New-HseCol H_FACILITY G Lookup 'Location / facility' 'Местоположение/Объект' G_IDENT -Registry HSE401_FACILITY))
    $c.Add((New-HseCol H_ACTIVITY H Lookup 'Type of activity' 'Вид деятельности' G_IDENT -Registry HSE401_ACTIVITY))
    $c.Add((New-HseCol H_PERMIT I Lookup 'Permit number' 'Номер разрешения' G_IDENT -Registry HSE401_PERMIT))
    $c.Add((New-HseCol H_EQUIP J Lookup 'Equipment type' 'Тип оборудования' G_IDENT -Registry HSE401_EQUIPTYPE))
    $c.Add((New-HseCol H_SRCTYPE K Lookup 'Emission Source Type' 'Тип источника эмиссии' G_IDENT -Registry HSE401_SRCTYPE))
    # Excel L (текст, 4 знаки з нулями, бувають суфікси 6354_DR) = вузол SRC дерева NAE; окремої формули-колонки MPE немає (з 05.11).
    $c.Add((New-HseCol H_NAE L Lookup 'MPE number' 'ПДВ номер' G_IDENT -Registry NAE_TREE -Filter 'level=SRC'))
    $c.Add((New-HseCol H_POLL O Lookup 'Pollutant code' 'Код загрязняющего вещества' G_POLL -Registry LAND_AIR_SUBSTANCE))
    $c.Add((New-HseCol H_COMPONENT P String 'Component' 'Компонент' G_POLL -Expr "REGFIELD([H_POLL], 'COMPONENT')" -Kind Formula -Width 220))
    $c.Add((New-HseCol H_PHASE Q String 'Phase of pollutant' 'Агрегатное состояние ЗВ' G_POLL -Expr "REGFIELD([H_POLL], 'PHASE')" -Kind Formula -Width 200))
    $c.Add((New-HseCol H_LIMIT_T R Decimal 'Permit emission, tons' 'Лимит, тонн' G_PERMIT -Precision 30 -Scale 16 -DisplayFormat $script:HseFmtLimit))
    $c.Add((New-HseCol H_LIMIT_GS S Decimal 'Permit emission, g/sec' 'Лимит, г/сек' G_PERMIT -Precision 30 -Scale 16 -DisplayFormat $script:HseFmtLimit))
    $c.Add((New-HseCol H_BASIS $null Decimal 'Operating days basis (blank = days in year)' 'База рабочих дней (пусто = дней в году)' G_BASIS -Precision 10 -Scale 3 -Kind NewInput))
    $c.Add((New-HseCol H_RATE $null Decimal 'Daily rate (helper)' 'Суточная норма (вспом.)' G_BASIS -Expr $RateExpr -Hidden $true -Kind Formula))
    for ($m = 1; $m -le 12; $m++) {
        $mm = '{0:00}' -f $m
        $c.Add((New-HseCol "H_T$mm" $script:HseMonthTon[$m - 1] Decimal 'Value, tons' 'Значение, тонн' "G_M$mm" -Expr "[H_RATE] * [H_D$mm]" -Kind Formula))
        $c.Add((New-HseCol "H_D$mm" $script:HseMonthWd[$m - 1] Decimal 'Work Days' 'Рабочих дней' "G_M$mm" -Precision 10 -Scale 4))
        if ($m % 3 -eq 0) {
            $q = $m / 3; $a = '{0:00}' -f ($m - 2); $b = '{0:00}' -f ($m - 1)
            $c.Add((New-HseCol "H_Q${q}_GS" $script:HseQtrGs[$q - 1] Decimal 'g/sec' 'г/сек' "G_Q$q" -Expr "IF(SUM([H_D$a], [H_D$b], [H_D$mm]) > 0, [H_LIMIT_GS], 0)" -Kind Formula))
            $c.Add((New-HseCol "H_Q${q}_T" $script:HseQtrTon[$q - 1] Decimal 'tonn' 'тонн' "G_Q$q" -Expr "SUM([H_T$a], [H_T$b], [H_T$mm])" -Kind Formula))
        }
    }
    $c.Add((New-HseCol H_RAW_NAME BB String 'Raw material / material name' 'Вид потребляемого сырья/материала (название)' G_RAWY -Width 200))
    $c.Add((New-HseCol H_RAW_T BC Decimal 'Raw material consumption, tons/year' 'Расход сырья/материала, тонн/год' G_RAWY -Precision 30 -Scale 16 -DisplayFormat $script:HseFmtRaw))
    $c.Add((New-HseCol H_RUN_H BD Decimal 'Equipment run time, hours/year' 'Время работы оборудования, часов/год' G_RAWY -Precision 24 -Scale 6 -DisplayFormat $script:HseFmtRaw))
    for ($q = 1; $q -le 4; $q++) {
        $a = '{0:00}' -f (3 * $q - 2); $b = '{0:00}' -f (3 * $q - 1); $z = '{0:00}' -f (3 * $q)
        $days = "IF([H_BASIS] = NULL, [Period].Days, [H_BASIS])"
        $c.Add((New-HseCol "H_RQ${q}_T" $script:HseRawQ[$q - 1][0] Decimal 'Raw material, tons/quarter' 'Расход сырья/материала, тонн/кв' "G_RQ$q" -Expr "[H_RAW_T] / $days * SUM([H_D$a], [H_D$b], [H_D$z])" -Kind Formula))
        $c.Add((New-HseCol "H_RQ${q}_H" $script:HseRawQ[$q - 1][1] Decimal 'Run time, hours/quarter' 'Время работы оборудования, часов/кв' "G_RQ$q" -Expr "[H_RUN_H] / $days * SUM([H_D$a], [H_D$b], [H_D$z])" -Kind Formula))
    }
    return , $c.ToArray()
}

function Get-Hse401Groups {
    <# Дерево груп заголовків (до 3 рівнів): код, батько, порядок, підписи. #>
    $g = New-Object System.Collections.Generic.List[object]
    function Add-G($code, $parent, $ord, $en, $ru) { $g.Add([pscustomobject]@{ Code = $code; Parent = $parent; Ordinal = $ord; En = $en; Ru = $ru }) }
    Add-G G_IDENT $null 1 'Emission source' 'Источник эмиссии'
    Add-G G_POLL $null 2 'Pollutant' 'Загрязняющее вещество'
    Add-G G_PERMIT $null 3 'Permit emission' 'Лимит по разрешению'
    Add-G G_BASIS $null 4 'Operating days basis' 'База рабочих дней'
    Add-G G_MONTHS $null 5 'Emissions by month and quarter' 'Выбросы по месяцам и кварталам'
    $o = 0
    for ($m = 1; $m -le 12; $m++) {
        $o++; Add-G ("G_M{0:00}" -f $m) G_MONTHS $o $script:HseMonthEn[$m - 1] $script:HseMonthRu[$m - 1]
        if ($m % 3 -eq 0) { $o++; Add-G ("G_Q{0}" -f ($m / 3)) G_MONTHS $o ("{0}Q" -f ($m / 3)) ("{0}кв" -f ($m / 3)) }
    }
    Add-G G_RAW $null 6 'Raw materials and run time' 'Сырьё и время работы'
    Add-G G_RAWY G_RAW 1 'Per year' 'За год'
    for ($q = 1; $q -le 4; $q++) { Add-G "G_RQ$q" G_RAW ($q + 1) "Q$q" "Q$q" }
    return , $g.ToArray()
}

function ConvertTo-HseL10n { param([string]$En, [string]$Ru) $h = @{ en = $En }; if ($Ru) { $h.ru = $Ru }; return $h }
