# tools/hse400/Hse400Methodology.Lib.ps1
# Чиста частина будівника HSE400: модель -> тіла запитів API і план методології. Мережі й бази не торкається.
#
# Договір із MonthColumnArgumentsModule (src/Ecr.Calculations, lane integ-hse400-month-module):
#   - таблиці LayoutKind = MonthsInColumns, 12 колонок IsMonthColumn/MonthNumber 1..12, усі шість на одному аркуші;
#   - аргумент = рядок вводу: GCV_<простір>_<таблиця>_<RowKey> (модуль дає і GCV.<...>);
#   - формула <простір>_<таблиця>_<X> -> рядок F_<X> тієї таблиці, код виходу = код колонки місяця;
#   - версія методології рівня Module; правило відбору рядків "{}" (без правил оркестратор не дає модулю жодного рядка);
#   - одиниця 'one' - запасна одиниця результату.
# Прив'язка (cfg.CalculationBinding) приймає лише колонку Calculated/Formula (ECR-TMPL-4227), а місячні колонки - ввід.
# Тому кожна таблиця має приховану колонку-якір типу Calculated: на ній висить прив'язка, модуль її не читає
# (бере лише IsMonthColumn).

Set-StrictMode -Version 2.0

$script:Hse400AnchorColumn = 'HSE400_Calc'
$script:Hse400CatchAllRule = 'R_HSE400_ALL'
$script:Hse400DimensionlessUnit = 'one'

function Get-Hse400AnchorColumnCode { return $script:Hse400AnchorColumn }
function Get-Hse400CatchAllRuleCode { return $script:Hse400CatchAllRule }

function Get-Hse400ArgumentName {
    <# Ім'я аргументу рядка вводу: GCV_<простір>_<таблиця>_<RowKey>. Рядок F_ аргументом не буває. #>
    param([Parameter(Mandatory)][string]$Namespace, [Parameter(Mandatory)][string]$Table, [Parameter(Mandatory)][string]$RowKey)
    if ($RowKey.StartsWith('F_', [StringComparison]::OrdinalIgnoreCase)) { throw "Рядок $Table/$RowKey розрахунковий: аргументом він не буває." }
    return ('GCV_{0}_{1}_{2}' -f $Namespace, $Table, $RowKey)
}

function Get-Hse400FormulaCode {
    <# Код формули розрахункового рядка F_<X>: <простір>_<таблиця>_<X>. #>
    param([Parameter(Mandatory)][string]$Namespace, [Parameter(Mandatory)][string]$Table, [Parameter(Mandatory)][string]$RowKey)
    if (-not $RowKey.StartsWith('F_', [StringComparison]::Ordinal) -or $RowKey.Length -le 2) { throw "Рядок $Table/$RowKey не розрахунковий (немає префікса F_)." }
    return ('{0}_{1}_{2}' -f $Namespace, $Table, $RowKey.Substring(2))
}

function Get-Hse400ModelList {
    <# Необов'язковий масив у розділі methodology (formulas/constants/tests); немає - порожній. #>
    param($Model, [string]$Name)
    if ($null -eq $Model.PSObject.Properties['methodology']) { return @() }
    $m = $Model.methodology
    if ($null -eq $m.PSObject.Properties[$Name] -or $null -eq $m.$Name) { return @() }
    return @($m.$Name)
}

function Get-Hse400MethodologyPlan {
    <#
    План методології з моделі: аргументи (рядки вводу), цілі (рядки F_ -> код формули), формули/константи/тести з
    моделі, прогалини (цілі без формули) і прив'язки (якір таблиці + вихід = формула першого рядка F_).
    Ready = формули покривають усі цілі і є хоч один тест (без тесту публікація відхиляється, ФВ-9.12).
    #>
    param([Parameter(Mandatory)]$Model)
    $ns = [string]$Model.methodology.code
    $args_ = New-Object System.Collections.Generic.List[object]
    $targets = New-Object System.Collections.Generic.List[object]
    $bindings = New-Object System.Collections.Generic.List[object]
    foreach ($t in @($Model.tables)) {
        $tc = [string]$t.code
        $first = $null
        foreach ($r in @($t.rows)) {
            $k = [string]$r.key
            if ([string]$r.kind -eq 'Calc') {
                $fc = Get-Hse400FormulaCode -Namespace $ns -Table $tc -RowKey $k
                $targets.Add([pscustomobject]@{ Table = $tc; RowKey = $k; FormulaCode = $fc })
                if ($null -eq $first) { $first = $fc }
            }
            else { $args_.Add([pscustomobject]@{ Table = $tc; RowKey = $k; Name = (Get-Hse400ArgumentName -Namespace $ns -Table $tc -RowKey $k) }) }
        }
        if ($null -ne $first) { $bindings.Add([pscustomobject]@{ Table = $tc; ColumnCode = $script:Hse400AnchorColumn; OutputCode = $first }) }
    }
    $formulas = @(Get-Hse400ModelList $Model 'formulas')
    $known = @{}
    foreach ($f in $formulas) { $known[([string]$f.code).ToLowerInvariant()] = $true }
    $missing = @($targets | Where-Object { -not $known.ContainsKey($_.FormulaCode.ToLowerInvariant()) })
    $tests = @(Get-Hse400ModelList $Model 'tests')
    # Виходи, які чернетка оголошує до публікації: прив'язка на неоголошений вихід - ECR-CALC-0422 bindingUnknownOutput,
    # а Generic (ним публікація рахує золоті тести) пише лише оголошені виходи.
    $outputs = New-Object System.Collections.Generic.List[string]
    foreach ($b in $bindings) { if (-not $outputs.Contains($b.OutputCode)) { $outputs.Add($b.OutputCode) } }
    foreach ($t in $tests) {
        $oc = Get-Hse400FormulaCode -Namespace $ns -Table ([string]$t.table) -RowKey ([string]$t.row)
        if (-not $outputs.Contains($oc)) { $outputs.Add($oc) }
    }
    return [pscustomobject]@{
        Namespace = $ns; Version = [string]$Model.methodology.version
        Arguments = $args_.ToArray(); Targets = $targets.ToArray(); Bindings = $bindings.ToArray(); Outputs = $outputs.ToArray()
        Formulas = $formulas; Constants = @(Get-Hse400ModelList $Model 'constants'); Tests = $tests
        MissingFormulas = $missing
        Ready = ($formulas.Count -gt 0 -and $missing.Count -eq 0 -and $tests.Count -gt 0)
    }
}

# ---------- тіла запитів (поля звірено з src/Ecr.Api; enum - рядками, EcrJsonSerialization) ----------

function New-Hse400TableBody {
    <# PUT /api/v1/template-versions/{vid}/sheets/{sheet}/tables/{code} (SaveTableDefRequest). #>
    param([Parameter(Mandatory)]$Table, [Parameter(Mandatory)][int]$Ordinal)
    return @{ nameL10n = @{ en = [string]$Table.en; ru = [string]$Table.ru }; ordinal = $Ordinal
        layoutKind = 'MonthsInColumns'; rowMode = 'Fixed'; maxDynamicRows = $null }
}

function New-Hse400MonthColumnBody {
    <# PUT …/tables/{tableId}/columns/M01..M12 (SaveColumnDefRequest): ввід, місячна колонка. #>
    param([Parameter(Mandatory)]$Month)
    return @{ headerL10n = @{ en = [string]$Month.En; ru = [string]$Month.Ru }; ordinal = [int]$Month.Month; dataType = 'Decimal'
        isRequired = $false; isReadOnly = $false; isHidden = $false; precision = $null; scale = $null
        defaultValue = $null; displayFormat = $null; styleId = $null; lookupRegistryDefId = $null; lookupFilter = $null
        unitId = $null; widthPx = 110; isMonthColumn = $true; monthNumber = [int]$Month.Month }
}

function New-Hse400AnchorColumnBody {
    <# Прихована колонка-якір прив'язки: Calculated, лише читання, не місячна (модуль її не читає). #>
    return @{ headerL10n = @{ en = 'Calculation (HSE400)'; ru = 'Расчет (HSE400)' }; ordinal = 13; dataType = 'Calculated'
        isRequired = $false; isReadOnly = $true; isHidden = $true; precision = $null; scale = $null
        defaultValue = $null; displayFormat = $null; styleId = $null; lookupRegistryDefId = $null; lookupFilter = $null
        unitId = $null; widthPx = $null; isMonthColumn = $null; monthNumber = $null }
}

function New-Hse400RowBody {
    <# PUT …/tables/{tableId}/rows/{key} (SaveRowDefRequest); рядок F_ - лише читання. #>
    param([Parameter(Mandatory)]$Row, [Parameter(Mandatory)][int]$Ordinal)
    return @{ labelL10n = @{ en = [string]$Row.en; ru = [string]$Row.ru }; ordinal = $Ordinal; rowKind = 'Item'; parentRowKey = $null
        isReadOnly = ([string]$Row.kind -eq 'Calc') }
}

function New-Hse400MethodologyBody {
    <# POST /api/v1/methodologies (CreateMethodologyRequest). Bespoke - як класифікує HSE400 імпорт AF. #>
    param([Parameter(Mandatory)]$Model)
    $m = $Model.methodology
    return @{ code = [string]$m.code; nameL10n = @{ en = [string]$m.en; ru = [string]$m.ru }; kind = 'Bespoke'; group = $null }
}

function New-Hse400MethodologyVersionBody {
    <# POST /api/v1/methodologies/{id}/versions (CreateMethodologyVersionRequest): порожня чернетка рівня Module. #>
    param([Parameter(Mandatory)]$Model)
    return @{ versionNumber = [string]$Model.methodology.version; copyFromVersionId = $null; level = 'Module' }
}

function New-Hse400CatchAllRuleBody {
    <# PUT …/versions/{vid}/rules/R_HSE400_ALL (SaveMethodologyRuleRequest): усі рядки таблиці. #>
    return @{ matchJson = '{}'; priority = 10; isActive = $true }
}

function New-Hse400FormulaBody {
    <# PUT …/versions/{vid}/formulas/{code} (SaveMethodologyFormulaRequest). #>
    param([Parameter(Mandatory)]$Formula)
    $csv = $null
    if ($Formula.PSObject.Properties['arguments'] -and $null -ne $Formula.arguments) { $csv = [string]$Formula.arguments }
    $rt = 'Number'
    if ($Formula.PSObject.Properties['resultType'] -and [string]$Formula.resultType) { $rt = [string]$Formula.resultType }
    return @{ expression = [string]$Formula.expression; resultType = $rt; outputUnitId = $null; argumentsCsv = $csv; scope = $null }
}

function New-Hse400ConstantBody {
    <#
    PUT …/versions/{vid}/constants/{code} (SaveMethodologyConstantRequest); одиниця - кодом каталогу.
    Числова константа без одиниці в моделі (у вивантаженні AF одиниць немає) отримує безрозмірну 'one':
    сервер числову константу без одиниці відхиляє (ECR-CALC-0422 constantNoUnit), а аргументи (місячні колонки) теж
    без одиниці.
    #>
    param([Parameter(Mandatory)]$Constant, [hashtable]$UnitIds = @{})
    $get = { param($n) if ($Constant.PSObject.Properties[$n]) { $Constant.$n } else { $null } }
    $kind = & $get 'kind'; if (-not $kind) { $kind = 'Numeric' }
    $unitId = $null
    $uc = & $get 'unit'
    if (-not $uc -and [string]$kind -eq 'Numeric') { $uc = $script:Hse400DimensionlessUnit }
    if ($uc) {
        if (-not $UnitIds.ContainsKey([string]$uc)) { throw "Константа $($Constant.code): одиниці '$uc' немає в каталозі." }
        $unitId = $UnitIds[[string]$uc]
    }
    return @{ kind = [string]$kind; value = (& $get 'value'); unitId = $unitId; textValue = (& $get 'textValue')
        validFrom = (& $get 'validFrom'); validTo = (& $get 'validTo'); category = (& $get 'category'); substanceEntryId = $null
        source = (& $get 'source') }
}

function ConvertTo-Hse400Decimal {
    <# Число моделі -> decimal без втрати знаків (ConvertFrom-Json дає double або decimal залежно від запису). #>
    param($Value)
    $inv = [Globalization.CultureInfo]::InvariantCulture
    if ($Value -is [decimal]) { return $Value }
    if ($Value -is [double] -or $Value -is [single]) { return [decimal]::Parse(([double]$Value).ToString('R', $inv), [Globalization.NumberStyles]::Float, $inv) }
    return [decimal]::Parse(([string]$Value), [Globalization.NumberStyles]::Float, $inv)
}

function New-Hse400GoldenTestBody {
    <#
    PUT …/versions/{vid}/tests/{code} (SaveMethodologyTestCaseRequest) для золотого тесту моделі.
    ⚠ Публікація проганяє тест одиничним ICalculationModule, а це GenericCalculationModule
    (src/Ecr.Calculations/DependencyInjection.cs), не модуль місячних колонок: він бере input.Arguments і пише лише
    ОГОЛОШЕНІ виходи (код = код формули). Тому вхід - ввід тесту аргументами в обох формах імені, які дає модуль
    місячних колонок (GCV.<простір>_<таблиця>_<рядок> і GCV_…), очікуване - { "<код формули>": число }, а вихід
    формули оголошує будівник (New-Hse400OutputBody). Адреса документа/періоду лишається: публікація вимагає
    справжній період (RequireTestPeriods). Стенд 11.11: адреса F_ без аргументів і ключ "M01" давали actual = null
    (ECR-CALC-0422 goldenSetDiverged).
    #>
    param([Parameter(Mandatory)]$Test, [Parameter(Mandatory)][long]$DocumentId, [Parameter(Mandatory)][long]$TableInstanceId, [Parameter(Mandatory)][int]$PeriodKey,
        [string]$Namespace = 'HSE400')
    $inv = [Globalization.CultureInfo]::InvariantCulture
    $table = [string]$Test.table
    $args_ = @()
    foreach ($p in $Test.inputs.PSObject.Properties) {
        $v = ConvertTo-Hse400Decimal $p.Value
        $plain = Get-Hse400ArgumentName -Namespace $Namespace -Table $table -RowKey $p.Name
        foreach ($n in @(('GCV.' + $plain.Substring(4)), $plain)) {
            $args_ += , [ordered]@{ argumentCode = $n; value = $v; valueString = $null; unitId = $null }
        }
    }
    $input_ = [ordered]@{ documentId = $DocumentId; tableInstanceId = $TableInstanceId; periodKey = [ordered]@{ value = $PeriodKey }
        sourceRowKey = [string]$Test.row; arguments = $args_ }
    $fc = Get-Hse400FormulaCode -Namespace $Namespace -Table $table -RowKey ([string]$Test.row)
    $expected = '{{"{0}":{1}}}' -f $fc, (ConvertTo-Hse400Decimal $Test.expected).ToString($inv)
    return @{ inputJson = ($input_ | ConvertTo-Json -Depth 6 -Compress); expectedJson = $expected; tolerance = (ConvertTo-Hse400Decimal $Test.tolerance) }
}

function New-Hse400OutputBody {
    <# PUT …/versions/{vid}/outputs/{код формули} (SaveMethodologyOutputRequest): вихід формули золотого тесту. #>
    param([Parameter(Mandatory)][int]$UnitId, [Parameter(Mandatory)][int]$Ordinal)
    return @{ unitId = $UnitId; ordinal = $Ordinal }
}

function Get-Hse400GoldenCellWrites {
    <#
    Ввід золотих тестів, згрупований за таблицею: таблиця -> перелік рядків PATCH /documents/{id}/cells
    (rowKey, cells = [{columnCode = місяць, isEmpty = false, value}]); baseVersion додає будівник із прочитаного зрізу.
    Два тести на ту саму комірку мусять давати те саме значення - інакше один із них перевіряв би чужий ввід.
    #>
    param([Parameter(Mandatory)]$Tests)
    $byCell = @{}
    $order = New-Object System.Collections.Generic.List[object]
    foreach ($t in @($Tests)) {
        foreach ($p in $t.inputs.PSObject.Properties) {
            $v = ConvertTo-Hse400Decimal $p.Value
            $k = ('{0}|{1}|{2}' -f [string]$t.table, $p.Name, [string]$t.month).ToLowerInvariant()
            if ($byCell.ContainsKey($k)) {
                if ($byCell[$k].Value -ne $v) { throw "Золоті тести дають різний ввід у $($t.table)/$($p.Name)/$($t.month): $($byCell[$k].Value) і $v." }
                continue
            }
            $cell = [pscustomobject]@{ Table = [string]$t.table; RowKey = $p.Name; Month = [string]$t.month; Value = $v }
            $byCell[$k] = $cell; $order.Add($cell)
        }
    }
    $result = [ordered]@{}
    foreach ($g in @($order | Group-Object Table)) {
        $rows = New-Object System.Collections.Generic.List[object]
        foreach ($r in @($g.Group | Group-Object RowKey)) {
            $cells = @($r.Group | ForEach-Object { @{ columnCode = $_.Month; isEmpty = $false; value = $_.Value } })
            $rows.Add(@{ rowKey = [string]$r.Name; cells = $cells })
        }
        $result[[string]$g.Name] = $rows.ToArray()
    }
    return $result
}

function New-Hse400CellsPatchBody {
    <# PATCH /api/v1/documents/{id}/cells: рядки одного екземпляра таблиці; baseVersion наявного рядка - його rowVersion. #>
    param([Parameter(Mandatory)][int]$PeriodKey, [Parameter(Mandatory)][long]$TableInstanceId, [Parameter(Mandatory)]$Rows, [hashtable]$RowVersions = @{})
    $prepared = @()
    foreach ($r in @($Rows)) {
        $bv = $null
        if ($RowVersions.ContainsKey([string]$r.rowKey)) { $bv = $RowVersions[[string]$r.rowKey] }
        $prepared += , @{ rowKey = [string]$r.rowKey; baseVersion = $bv; cells = @($r.cells) }
    }
    return @{ origin = 'UserEdit'; periodKey = $PeriodKey; tableInstanceId = $TableInstanceId; rows = $prepared }
}

function New-Hse400MethodologyPublishBody {
    <# POST …/versions/{vid}/publish (PublishMethodologyRequest). #>
    param([Parameter(Mandatory)]$Model)
    $from = $null
    if ($Model.methodology.PSObject.Properties['effectiveFrom']) { $from = [string]$Model.methodology.effectiveFrom }
    return @{ changeReason = "HSE400 $($Model.methodology.version): gas composition, month columns module"; effectiveFrom = $from }
}

function New-Hse400BindingBody {
    <# PUT /api/v1/methodologies/{id}/bindings/{columnDefId}/{outputCode} (SaveCalculationBindingRequest). #>
    param([bool]$Active = $true)
    return @{ matchJson = '{}'; isActive = $Active }
}

# ---------- перевірки відповідей ----------

function Find-Hse400UnitId {
    <# Id одиниці з переліку GET /api/v1/units (UnitRef: id, code); немає - виняток із поясненням. #>
    param($Units, [string]$Code = $script:Hse400DimensionlessUnit)
    $u = @($Units) | Where-Object { $null -ne $_ -and [string]$_.code -ceq $Code } | Select-Object -First 1
    if ($null -eq $u) {
        throw "Одиниці '$Code' немає в каталозі (GET /api/v1/units). Модуль місячних колонок бере її як запасну одиницю результату формул HSE400; без неї результати без одиниці відкидаються. Заведіть безрозмірну одиницю з кодом '$Code' (сід одиниць) і повторіть."
    }
    return [int]$u.id
}

function Test-Hse400Structure {
    <# Розбіжності опублікованої структури з договором модуля (GET /template-versions/{vid}/structure); порожньо - згідно. #>
    param([Parameter(Mandatory)]$Structure, [Parameter(Mandatory)]$Model)
    $bad = New-Object System.Collections.Generic.List[string]
    $sheet = @($Structure.sheets) | Where-Object { [string]$_.code -eq [string]$Model.sheet.code } | Select-Object -First 1
    if ($null -eq $sheet) { $bad.Add("немає аркуша $($Model.sheet.code)"); return $bad.ToArray() }
    foreach ($t in @($Model.tables)) {
        $tb = @($sheet.tables) | Where-Object { [string]$_.code -eq [string]$t.code } | Select-Object -First 1
        if ($null -eq $tb) { $bad.Add("немає таблиці $($t.code)"); continue }
        if ([string]$tb.layoutKind -ne 'MonthsInColumns') { $bad.Add("$($t.code): layoutKind $($tb.layoutKind)") }
        $cols = @{}
        foreach ($c in @($tb.columns)) { $cols[[string]$c.code] = [string]$c.dataType }
        for ($i = 1; $i -le 12; $i++) { $mc = 'M{0:00}' -f $i; if (-not $cols.ContainsKey($mc)) { $bad.Add("$($t.code): немає колонки $mc") } }
        if (-not $cols.ContainsKey($script:Hse400AnchorColumn)) { $bad.Add("$($t.code): немає колонки-якоря $script:Hse400AnchorColumn") }
        elseif ($cols[$script:Hse400AnchorColumn] -ne 'Calculated') { $bad.Add("$($t.code): $script:Hse400AnchorColumn типу $($cols[$script:Hse400AnchorColumn])") }
    }
    return $bad.ToArray()
}

function Get-Hse400AnchorColumnIds {
    <# Таблиця -> Id колонки-якоря з GET /template-versions/{vid}/structure. #>
    param([Parameter(Mandatory)]$Structure, [Parameter(Mandatory)]$Model)
    $ids = @{}
    $sheet = @($Structure.sheets) | Where-Object { [string]$_.code -eq [string]$Model.sheet.code } | Select-Object -First 1
    if ($null -eq $sheet) { return $ids }
    foreach ($tb in @($sheet.tables)) {
        $c = @($tb.columns) | Where-Object { [string]$_.code -eq $script:Hse400AnchorColumn } | Select-Object -First 1
        if ($null -ne $c) { $ids[[string]$tb.code] = [int]$c.id }
    }
    return $ids
}
