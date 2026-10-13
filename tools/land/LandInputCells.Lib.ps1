# tools/land/LandInputCells.Lib.ps1
# Чиста логіка (без мережі й без БД) точкових завантажувачів вхідних комірок Land: вид TypeFuel
# (Land_TypeFuel на T10_01/T10_03/T10_04/T11_01 + ReleaseOnColdVentYesNo_M<мм> на T10_01/T11_01) і вид Gtu
# (Land_Measure_GTU_Month, Land_Measure_GTU<1..5>_<301|304|330|337|O2> на T10_03/T11_03). Використовує
# Load-LandInputCells.ps1; тестується на синтетичних даних. Значень прода тут немає: лише коди таблиць/колонок.

function Get-LandInputCellKind {
    <#
    .SYNOPSIS
        Опис виду завантаження: відповідність таблиць AF -> код таблиці й аркуш, джерела колонок.
    .PARAMETER Name
        TypeFuel | Gtu.
    #>
    param([Parameter(Mandatory)][ValidateSet('TypeFuel', 'Gtu')][string]$Name)

    if ($Name -eq 'TypeFuel') {
        return [pscustomobject]@{
            Name       = 'TypeFuel'
            TableMap   = @{
                'AF_Air_Land_Off_Utility_DI_10_01' = 'T10_01'
                'AF_Air_Land_Off_Utility_DI_10_03' = 'T10_03'
                'AF_Air_Land_Off_Utility_DI_10_04' = 'T10_04'
                'AF_Air_Land_Off_Process_DI_11_01' = 'T11_01'
            }
            SheetOf    = @{ T10_01 = 'S10_OFF_UTILITY_DI'; T10_03 = 'S10_OFF_UTILITY_DI'; T10_04 = 'S10_OFF_UTILITY_DI'; T11_01 = 'S11_OFF_PROCESS_DI' }
            ColdVentTables = @('T10_01', 'T11_01')
            # Реєстри за КОДОМ - запасний варіант, якщо колонка не несе lookupRegistryDefId (id реєстрів між стендами різні).
            TypeFuelRegistry = 'LAND_STATIONARY_TYPE_OF_FUEL'
            ColdVentRegistry = 'LAND_COLD2_VENTS_YES_NO'
        }
    }
    $gtuColumns = New-Object System.Collections.Generic.List[string]
    $gtuColumns.Add('Land_Measure_GTU_Month')
    foreach ($g in 1..5) { foreach ($c in '301', '304', '330', '337', 'O2') { $gtuColumns.Add(('Land_Measure_GTU{0}_{1}' -f $g, $c)) } }
    return [pscustomobject]@{
        Name       = 'Gtu'
        TableMap   = @{ 'AF_Air_Land_Off_Utility_DI_10_03' = 'T10_03'; 'AF_Air_Land_Off_Process_DI_11_03' = 'T11_03' }
        SheetOf    = @{ T10_03 = 'S10_OFF_UTILITY_DI'; T11_03 = 'S11_OFF_PROCESS_DI' }
        Columns    = $gtuColumns.ToArray()
        TextColumn = 'Land_Measure_GTU_Month'
    }
}

function Test-LandInputCellEmpty {
    <# TRUE для порожнього значення джерела: $null, '' або літерал NULL. #>
    param($Value)
    if ($null -eq $Value) { return $true }
    $s = [string]$Value
    return ($s -eq '' -or $s -eq 'NULL')
}

function Get-LandRowKeyPrefix {
    <#
    .SYNOPSIS
        Префікс ключа рядка з імені документа: `p<кінцеві цифри>` (ім'я `...-10` -> `p10`); рядок `p<NN>-<0001>` будує
        завантажувач повного набору. Немає кінцевих цифр - відмова: ключі рядків вгадувати не можна.
    #>
    param([Parameter(Mandatory)][string]$DocumentName)
    $m = [regex]::Match($DocumentName.Trim(), '(\d+)$')
    if (-not $m.Success) { throw "Ім'я документа '$DocumentName' не закінчується цифрами: префікс ключа рядка не визначити." }
    return 'p' + $m.Groups[1].Value
}

function New-LandInputCellPlan {
    <#
    .SYNOPSIS
        План завантаження з JSON одного документа: ключ "<документ>|<періодKey>|<таблиця>" -> список рядків
        { RowKey, Cells[{ Col, Text, Kind }] }. Порожні значення пропускаються; рядки без комірок - теж.
    .PARAMETER KindName    TypeFuel | Gtu.
    .PARAMETER DocumentName Ім'я документа (з нього береться префікс ключа рядка).
    .PARAMETER Document    Розібраний JSON (IDictionary): periods['YYYY-MM'][<таблиця AF>].rows[].
    .PARAMETER Periods     Обмеження періодів (Рік*100+Місяць); порожньо - усі періоди файла.
    .PARAMETER ModelColumns Код таблиці -> колонки GTU з моделі (Get-LandModelTableColumns). Вид Gtu бере з файла лише їх;
                           GTU-колонка файла, якої в моделі цієї таблиці немає (T10_03 не має GTU5), не йде в план, а
                           лічиться в -SourceWithoutTarget ("таблиця/колонка" -> кількість непорожніх комірок).
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('TypeFuel', 'Gtu')][string]$KindName,
        [Parameter(Mandatory)][string]$DocumentName,
        [Parameter(Mandatory)]$Document,
        [int[]]$Periods = @(),
        [hashtable]$ModelColumns,
        [hashtable]$SourceWithoutTarget
    )

    $def = Get-LandInputCellKind -Name $KindName
    $prefix = Get-LandRowKeyPrefix -DocumentName $DocumentName
    $built = [ordered]@{}
    $periodsNode = $Document['periods']
    if ($null -eq $periodsNode) { throw "У JSON документа '$DocumentName' немає вузла periods." }
    foreach ($pk in @($periodsNode.Keys)) {
        $month = $pk.Substring(5, 2)
        $periodNumber = [int]($pk.Replace('-', ''))
        if ($Periods.Count -gt 0 -and $Periods -notcontains $periodNumber) { continue }
        $tablesNode = $periodsNode[$pk]
        foreach ($afTable in @($tablesNode.Keys)) {
            if (-not $def.TableMap.ContainsKey($afTable)) { continue }
            $tableCode = $def.TableMap[$afTable]
            $seq = 0
            $list = New-Object System.Collections.ArrayList
            foreach ($row in @($tablesNode[$afTable]['rows'])) {
                $seq++
                $rowKey = '{0}-{1:0000}' -f $prefix, $seq
                $cells = New-Object System.Collections.ArrayList
                if ($KindName -eq 'TypeFuel') {
                    if ($row.ContainsKey('Land_TypeFuel') -and -not (Test-LandInputCellEmpty $row['Land_TypeFuel'])) {
                        [void]$cells.Add([pscustomobject]@{ Col = 'Land_TypeFuel'; Text = [string]$row['Land_TypeFuel']; Kind = 'TypeFuel' })
                    }
                    if ($def.ColdVentTables -contains $tableCode -and $row.ContainsKey('Land_Measure_ReleaseColdVent') -and
                        -not (Test-LandInputCellEmpty $row['Land_Measure_ReleaseColdVent'])) {
                        [void]$cells.Add([pscustomobject]@{ Col = "ReleaseOnColdVentYesNo_M$month"; Text = [string]$row['Land_Measure_ReleaseColdVent']; Kind = 'ColdVent' })
                    }
                }
                else {
                    $known = $null
                    if ($null -ne $ModelColumns -and $ModelColumns.ContainsKey($tableCode)) { $known = @($ModelColumns[$tableCode]) }
                    foreach ($col in @($row.Keys | Sort-Object)) {
                        if ([string]$col -notmatch '^Land_Measure_GTU(_Month|\d_(301|304|330|337|O2))$') { continue }
                        if (Test-LandInputCellEmpty $row[$col]) { continue }
                        $inModel = if ($null -ne $known) { $known -contains [string]$col } else { $def.Columns -contains [string]$col }
                        if (-not $inModel) {
                            if ($null -ne $known -and $null -ne $SourceWithoutTarget) {
                                $orphanKey = "$tableCode/$col"
                                $bag = $SourceWithoutTarget
                                $bag[$orphanKey] = 1 + [int]$bag[$orphanKey]
                            }
                            continue
                        }
                        [void]$cells.Add([pscustomobject]@{ Col = [string]$col; Text = [string]$row[$col]; Kind = 'Gtu' })
                    }
                }
                if ($cells.Count -gt 0) { [void]$list.Add([pscustomobject]@{ RowKey = $rowKey; Cells = $cells.ToArray() }) }
            }
            $planKey = "$DocumentName|$periodNumber|$tableCode"
            $built[$planKey] = $list
        }
    }
    return $built
}

function Get-LandInputCellPlanSummary {
    <# Рядки підсумку плану (лише лічильники): Key, Rows, Cells, TypeFuel, ColdVent, Gtu. #>
    param([Parameter(Mandatory)]$Plan)
    foreach ($key in @($Plan.Keys)) {
        $all = @($Plan[$key] | ForEach-Object { $_.Cells })
        [pscustomobject]@{
            Key      = [string]$key
            Rows     = @($Plan[$key]).Count
            Cells    = $all.Count
            TypeFuel = @($all | Where-Object { $_.Kind -eq 'TypeFuel' }).Count
            ColdVent = @($all | Where-Object { $_.Kind -eq 'ColdVent' }).Count
            Gtu      = @($all | Where-Object { $_.Kind -eq 'Gtu' }).Count
        }
    }
}

function New-LandLookupMap {
    <# Записи довідника -> хеш «нормалізований текст -> id» (display і code, без урахування регістру). #>
    param([AllowNull()][object[]]$Entries)
    $lookup = @{}
    foreach ($e in @($Entries)) {
        if ($null -eq $e) { continue }
        $display = ([string]$e.display).ToLowerInvariant()
        if ($display) { $lookup[$display] = [int]$e.id }
        $codeKey = ([string]$e.code).ToLowerInvariant() -replace '[^a-z0-9<>=]', ''
        if ($codeKey) { $lookup[$codeKey] = [int]$e.id }
    }
    return $lookup
}

function Resolve-LandLookupId {
    <#
    .SYNOPSIS
        id запису довідника за текстом значення: англійська частина до « - », нормалізований код, повний текст.
        $null - значення в довіднику не знайдено (лічиться як lookupMiss, не вгадується).
    #>
    param([Parameter(Mandatory)][hashtable]$Map, [Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    $full = $Text.Trim()
    $head = ($full -split ' - ')[0].Trim().ToLowerInvariant()
    if ($Map.ContainsKey($head)) { return [int]$Map[$head] }
    $headKey = $head -replace '[^a-z0-9<>=]', ''
    if ($headKey -and $Map.ContainsKey($headKey)) { return [int]$Map[$headKey] }
    $lower = $full.ToLowerInvariant()
    if ($Map.ContainsKey($lower)) { return [int]$Map[$lower] }
    $fullKey = $lower -replace '[^a-z0-9<>=]', ''
    if ($fullKey -and $Map.ContainsKey($fullKey)) { return [int]$Map[$fullKey] }
    return $null
}

function ConvertTo-LandInputNumber {
    <# Число з тексту за InvariantCulture; $null, якщо не число (NaN/Infinity теж відхиляються). #>
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Text)
    $number = 0.0
    $ok = [double]::TryParse($Text.Trim(), [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$number)
    if (-not $ok -or [double]::IsNaN($number) -or [double]::IsInfinity($number)) { return $null }
    return $number
}

function ConvertTo-LandStringList {
    <# Список імен із параметра: масив або рядок «a,b» / «a;b» (powershell -File віддає «a,b» одним рядком). #>
    param($Value)
    $items = New-Object System.Collections.Generic.List[string]
    foreach ($v in @($Value)) {
        if ($null -eq $v) { continue }
        foreach ($t in ([string]$v -split '[,;]')) {
            $s = $t.Trim()
            if ($s.Length -gt 0 -and -not $items.Contains($s)) { $items.Add($s) }
        }
    }
    return $items.ToArray()
}

function ConvertFrom-LandLargeJson {
    <#
    .SYNOPSIS
        Великий JSON (повний набір документа, десятки МБ) -> словники/масиви. Windows PowerShell 5.1: JavaScriptSerializer
        (ConvertFrom-Json там повільний і має ліміти); PowerShell 7: ConvertFrom-Json -AsHashtable.
    #>
    param([Parameter(Mandatory)][string]$Text)

    $serializerType = $null
    try {
        Add-Type -AssemblyName System.Web.Extensions -ErrorAction Stop
        $serializerType = [System.Web.Script.Serialization.JavaScriptSerializer]
    }
    catch { $serializerType = $null }
    if ($null -ne $serializerType) {
        $ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $ser.MaxJsonLength = [int]::MaxValue
        $ser.RecursionLimit = 200
        return $ser.DeserializeObject($Text)
    }
    return ($Text | ConvertFrom-Json -AsHashtable -Depth 100)
}
function Get-LandModelTableColumns {
    <#
    .SYNOPSIS
        Код таблиці -> коди колонок моделі Land, що відповідають шаблону (за замовчуванням GTU-входи). Це перелік, який
        очікує стенд, побудований із моделі: різні таблиці мають різну кількість груп (T10_03 - GTU1..4, T11_03 - GTU1..5).
    #>
    param(
        [Parameter(Mandatory)]$Model,
        [Parameter(Mandatory)][string[]]$TableCodes,
        [string]$Pattern = '^Land_Measure_GTU(_Month|\d_(301|304|330|337|O2))$'
    )
    $found = @{}
    foreach ($sheet in @($Model.sheets)) {
        foreach ($tbl in @($sheet.tables)) {
            $code = [string]$tbl.code
            if ($TableCodes -notcontains $code -or $found.ContainsKey($code)) { continue }
            $found[$code] = @(@($tbl.columns) | ForEach-Object { [string]$_.code } | Where-Object { $_ -match $Pattern })
        }
    }
    return $found
}