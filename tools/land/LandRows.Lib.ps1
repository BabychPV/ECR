# tools/land/LandRows.Lib.ps1
# Pure logic (no network, no database) of Load-LandRows.ps1: the full-set row loader of Land documents.
# Ported from the out-of-repo Load-Full.ps1 (F:\ecr-wt\land-full-load) with the LAND-5094 fixes:
#   * rowKey is table-qualified: p<doc>-<TableCode>-<seq:0000> (LandRowKey.Lib.ps1), unique across the tables of a document;
#   * NO row cap by default (the old cap was the Excel physical row count of the template, e.g. 498 for T3_00, 184 for T9_04,
#     not MaxDynamicRows): -MaxRowsPerTable 0 = no cap, > 0 = explicit cap, counted as `capped`;
#   * column mapping is taken from the model (prodColumnMapping aliases, incl. month columns with irregular codes:
#     T9_04 SubstanceConsumptionKg/KgH, T11c/d No_* -> FB_*No, ReleaseColdVent, NumberOperations, GTU, ProcessTrain),
#     then from the built-in legacy aliases and same-name / _M<mm> / Ref candidates;
#   * a source column with no target is only COUNTED (`unmapped`); a template input column with no source is COUNTED
#     (`sourceWithoutColumn`); nothing is invented.
# No production values are produced here: only codes and counters.
# Naming rule (LandScriptVariableCollisionTests): locals never share a name with a parameter of this file.

. (Join-Path $PSScriptRoot 'LandRowKey.Lib.ps1')
. (Join-Path $PSScriptRoot 'LandInputCells.Lib.ps1')
. (Join-Path $PSScriptRoot 'LandNaeType.Lib.ps1')

$script:LandRowsLegacyAliases = @{
    'Land_Rotation' = 'Land_RotationRef'; 'Land_PurposeEquipment' = 'Land_Purpose'
    'Land_InvNumEqp' = 'Land_InventoryNumber|InventoryNumberOfEquipment'; 'Land_DescEqp' = 'Land_DescriptionEquipment|DescriptionOfEquipment'
    'Land_TypeEmissionSource' = 'TypeOfEmissionSourceWithinUnit'; 'Land_TypEmissioSource' = 'TypeOfEmissionSourceWithinUnit'
    'Land_TypeFuel' = 'TypeOfFuel|FuelType'; 'Land_TypeFuelOrStream' = 'TypeOfFuel'
    'Land_NAE' = 'IDCodeNAEProject|IDCodeMPEProject'; 'Land_Status' = 'Status'; 'Land_No' = 'No'; 'Land_Subcontractor' = 'Subcontractor'; 'Land_Area' = 'Area'
    'Land_TypeEqp' = 'TypeOfEquipment|Land_TypeEquipment'; 'Land_Streams' = 'Streams'; 'Land_ProcessTrain' = 'ProcessTrain'
    'Land_TypeStreams' = 'TypeOfStreams'; 'Land_Power' = 'PowerKW'
    'Land_Measure_OperatingTime' = 'OperatingTime|OperatingTimeReleaseOnColdVentYesNo'
    'Land_Measure_SubstanceConsumption' = 'SubstanceConsumption'; 'Land_Measure_RunningHours' = 'RunningHoursPerMonth'
    'Land_Measure_FuelConsumption' = 'FuelConsumption'; 'Land_Stream' = 'Stream'; 'Land_TypeStorMater' = 'Land_TypeStorMaterial'
    'Land_FlowRateSulfur' = 'FlowRateOfSulfur'; 'Land_Measure_MaterialResidence' = 'MaterialResidenceTimeInProcessUnits'
    'Land_Measure_FuelSubstanceConsumptionOrPumpedVolume' = 'FuelConsumptionPumpedVolume|SubstanceConsumptionOrPumpedVolume|PumpedVolume'
    'Land_Measure_GasConsumption' = 'GasConsumption|GasCosumption'; 'Land_Measure_MaximumConsumption' = 'MaximumConsumption'
    'Land_ProcessUnits' = 'ProcessUnits'; 'Land_TankVolume' = 'TankVolume'; 'Land_PumpingCapacity' = 'PumpingCapacity'
    'Land_TypeStoredMaterial' = 'TypeOfStoredMaterial'; 'Land_Measure_QuantityLiquid' = 'QuantityOfLiquidAddedToTank'
    'Land_Kpmax' = 'KpMaxExperimentalFactor'; 'Land_Gxp' = 'GxpStorageEmissionsOfHCVapourFromOne'
    'Land_Vhmax' = 'VMaxTheMaximumVolumeOfTheSteamAirMix'; 'Land_TypeStream' = 'TypeOfStreams'
    'No_DMSpump' = 'FB_DMSPumpNo'; 'No_Pump' = 'PumpNo|FB_PumpNo'; 'No_Flanges' = 'FlangesNo|FB_FlangesNo'; 'No_Valves' = 'ValvesNo|FB_ValvesNo'
    'No_SafValves' = 'SafValvesNo|FB_SafValvesNo'
    'Land_Measure_SubstanceConsumptionSm3' = 'SubstanceConsumption20Dgr1atm'; 'Land_OperatingTime' = 'OperatingTime'
    'Land_Measure_ReleaseColdVent' = 'ReleaseOnColdVentYesNo'; 'Land_Measure_NumberOperations' = 'NumberOfOperationsN'
    # T6_12 (C08_04_03, diagnosis D): the Land_Agent_* / Land_TrappingEfficienty columns are formulas (read-only) in ECR,
    # the editable inputs carry the long K1.../B1... names (the stand names are truncated to 36 characters).
    'Land_Agent_Fraction' = 'K1ShareOfDustFractionInTheMaterial'; 'Land_Agent_Transforming' = 'K2ShareOfVolatileDustTransformingInA'
    'Land_Agent_LocalMeteorologicalConditions' = 'K3AvrFactorTakingIntoAccountLocalMet'; 'Land_Agent_LocalMeteorologicalConditionsMax' = 'K3MaxFactorTakingIntoAccountLocalMet'
    'Land_Agent_LocalConditions' = 'K4FactorTakingIntoAccountLocalCondit'; 'Land_Agent_MoistureMaterial' = 'K5FactorTakingIntoAccountMoistureOfM'
    'Land_Agent_CoarsenessMaterial' = 'K7FactorTakingIntoAccountCoarsenessO'; 'Land_Agent_MaterialsGrabber' = 'K8CorrectionFactorForDifferentMateri'
    'Land_Agent_SalvoEmission' = 'K9CorrectionFactorUnderForcefulSalvo'; 'Land_Agent_AccountLoading' = 'B1FactorTakingIntoAccountLoadingHeig'
    'Land_TrappingEfficienty' = 'DustTrappingEfficiency'; 'Land_TypeMaterial' = 'TypeOfMaterial'
    'Land_Agent_EmissionSourceType' = 'EmissionSourceType'; 'Land_Remarks' = 'WriteYourRemarksHere'
}

function Get-LandRowsTableKey {
    <# Prod AF table -> 'SHEET|TABLE' of the template, or $null (unknown table). Water/waste tables return their key too. #>
    param([Parameter(Mandatory)][string]$ProdTable)
    switch -regex ($ProdTable) {
        '^AF_Air_Land_Stationary_Equipment$' { return 'S03_STATIONARY_EQUIPMENT|T3_00' }
        '^AF_Air_Land_Mobile_Equipment$' { return 'S04_MOBILE_EQUIPMENT|T4_01' }
        '^AF_Air_Land_Other_Equipment_(5_\d+)$' { return 'S05_OTHER_EQUIPMENT|T' + $Matches[1] }
        '^AF_Air_Land_Dust_Producing_Activities_(6_\d+)$' { return 'S06_DUST_PRODUCING_ACTIVITIES|T' + $Matches[1] }
        '^AF_Air_Land_Utility_(9_\d+)$' { return 'S09_UTILITY|T' + $Matches[1] }
        '^AF_Air_Land_Off_Utility_DI_(10_\d+)$' { return 'S10_OFF_UTILITY_DI|T' + $Matches[1] }
        '^AF_Air_Land_Off_Utility_AI_(10a_\d+)$' { return 'S10A_OFF_UTILITY_AI|T' + $Matches[1] }
        '^AF_Air_Land_Off_Process_DI_(11_\d+)$' { return 'S11_OFF_PROCESS_DI|T' + $Matches[1] }
        '^AF_Air_Land_11([abcd])_Off_Process' {
            $sheetOf = @{ a = 'S11A_OFF_PROCESS_AI'; b = 'S11B_OFF_PROCESS_EPC2'; c = 'S11C_OFF_PROCESS_EPC3'; d = 'S11D_OFF_PROCESS_EPC4' }
            return $sheetOf[$Matches[1]] + '|T11' + $Matches[1] + '_01'
        }
        '^AF_Water_Land_07$' { return 'S07_WATER_REPORT|T7_00' }
        '^AF_Water_Land_07a_(\d+)$' { return 'S07A_WATER_MEASUREMENTS|T7a_' + ('{0:00}' -f [int]$Matches[1]) }
        '^AF_Waste_Land_08$' { return 'S08_WASTE_REPORT|T8_00' }
        default { return $null }
    }
}

function Test-LandRowsSpecialTable {
    <# Water / waste tables use item row keys of the template, not positional keys: this loader does not load them. #>
    param([Parameter(Mandatory)][string]$TableKey)
    return ($TableKey -match '^(S07_WATER_REPORT|S07A_WATER_MEASUREMENTS|S08_WASTE_REPORT)\|')
}

function Get-LandRowsModelAliasMap {
    <# Model prodColumnMapping -> 'SHEET|TABLE' -> prod column -> @{ Column = code | $null; MonthColumns = string[] }. #>
    param([Parameter(Mandatory)]$Model)
    $byTable = @{}
    $pcm = $Model.prodColumnMapping
    if ($null -eq $pcm -or $null -eq $pcm.tables) { return $byTable }
    foreach ($tp in @($pcm.tables.PSObject.Properties)) {
        $perProd = @{}
        foreach ($al in @($tp.Value.aliases)) {
            $single = $null; $months = @()
            if ($al.PSObject.Properties.Match('column').Count -gt 0 -and $al.column) { $single = [string]$al.column }
            if ($al.PSObject.Properties.Match('monthColumns').Count -gt 0 -and $al.monthColumns) {
                $months = @($al.monthColumns | ForEach-Object { [string]$_ })
                if ($months.Count -eq 1 -and $months[0] -match '\s') { $months = @($months[0] -split '\s+' | Where-Object { $_ }) }
            }
            $perProd[[string]$al.prod] = @{ Column = $single; MonthColumns = $months }
        }
        $byTable[[string]$tp.Name] = $perProd
    }
    return $byTable
}

function Get-LandRowsModelColumns {
    <# Offline column map of one table from the model: code -> @{ code; dataType; readOnly; expr; reg }. $null when absent. #>
    param([Parameter(Mandatory)]$Model, [Parameter(Mandatory)][string]$TableKey)
    $shCode, $tbCode = $TableKey -split '\|', 2
    foreach ($sheet in @($Model.sheets)) {
        if ([string]$sheet.code -ne $shCode) { continue }
        foreach ($tbl in @($sheet.tables)) {
            if ([string]$tbl.code -ne $tbCode) { continue }
            $map = @{}
            foreach ($c in @($tbl.columns)) {
                $dt = [string]$c.dataType
                $ro = [bool]($c.readOnly -or $dt -in 'Formula', 'Calculated')
                $ex = $null; if ($c.PSObject.Properties.Match('expression').Count -gt 0) { $ex = $c.expression }
                $map[[string]$c.code] = @{ code = [string]$c.code; dataType = $dt; readOnly = $ro; expr = $ex; reg = $null }
            }
            return $map
        }
    }
    return $null
}

function ConvertTo-LandRowsStandColumnMap {
    <# Stand table response columns -> the same shape as Get-LandRowsModelColumns. #>
    param([Parameter(Mandatory)]$StandColumns)
    $map = @{}
    foreach ($c in @($StandColumns)) {
        $reg = $null; if ($c.PSObject.Properties.Match('lookupRegistryDefId').Count -gt 0 -and $c.lookupRegistryDefId) { $reg = [int]$c.lookupRegistryDefId }
        $ex = $null; if ($c.PSObject.Properties.Match('expression').Count -gt 0) { $ex = $c.expression }
        $dt = [string]$c.dataType
        $map[[string]$c.code] = @{ code = [string]$c.code; dataType = $dt; readOnly = [bool]($c.isReadOnly -or $dt -in 'Formula', 'Calculated'); expr = $ex; reg = $reg }
    }
    return $map
}

function Test-LandRowsInputColumn {
    param($ColumnDef)
    return ($null -ne $ColumnDef -and -not $ColumnDef.readOnly -and -not $ColumnDef.expr)
}

function Resolve-LandRowsColumn {
    <#
    .SYNOPSIS
        Prod column + month -> template column. Returns @{ Def = column | $null; MissingTarget = code | $null }.
        MissingTarget: the model alias names a column the table (stand) does not have - a finding, not a silent skip.
    #>
    param(
        [Parameter(Mandatory)][string]$ProdColumn,
        [Parameter(Mandatory)][int]$MonthNo,
        [Parameter(Mandatory)][hashtable]$ColumnMap,
        [hashtable]$TableAliases
    )
    $mm = '{0:00}' -f $MonthNo
    if ($null -ne $TableAliases -and $TableAliases.ContainsKey($ProdColumn)) {
        $al = $TableAliases[$ProdColumn]
        $target = $al.Column
        if (-not $target -and @($al.MonthColumns).Count -ge $MonthNo) { $target = @($al.MonthColumns)[$MonthNo - 1] }
        if ($target) {
            if ($ColumnMap.ContainsKey($target)) {
                if (Test-LandRowsInputColumn $ColumnMap[$target]) { return @{ Def = $ColumnMap[$target]; MissingTarget = $null } }
                return @{ Def = $null; MissingTarget = $null }
            }
            return @{ Def = $null; MissingTarget = $target }
        }
    }
    $names = @($ProdColumn)
    if ($script:LandRowsLegacyAliases.ContainsKey($ProdColumn)) { $names += @($script:LandRowsLegacyAliases[$ProdColumn] -split '\|') }
    foreach ($n in $names) {
        foreach ($cand in @($n, "${n}_M$mm")) {
            if ($ColumnMap.ContainsKey($cand) -and (Test-LandRowsInputColumn $ColumnMap[$cand])) { return @{ Def = $ColumnMap[$cand]; MissingTarget = $null } }
        }
    }
    foreach ($n in $names) {
        foreach ($cand in @("${n}Ref", "${n}Ref_M$mm")) {
            if ($ColumnMap.ContainsKey($cand) -and $ColumnMap[$cand].dataType -eq 'Lookup' -and (Test-LandRowsInputColumn $ColumnMap[$cand])) {
                return @{ Def = $ColumnMap[$cand]; MissingTarget = $null }
            }
        }
    }
    return @{ Def = $null; MissingTarget = $null }
}

function New-LandRowsTablePlan {
    <#
    .SYNOPSIS
        One table-period of one document: source rows -> rows with cells (positional table-qualified rowKey).
    .PARAMETER DocNumber       number of LAND-PROD-<n>
    .PARAMETER TableCode       template table code (T3_00)
    .PARAMETER LookupResolver  scriptblock($RegistryId, $Text) -> entry id | $null; absent = offline (cell counted, id 0)
    .PARAMETER RowLimit        0 = no cap
    .OUTPUTS @{ Rows; ProdRows; Capped; LookupMiss; LookupUnresolved; MixedText; MixedNumber; Type3InMixed; Unmapped (sorted names); Mapped (names); MissingTargets; DupCells; Cells }
    #>
    param(
        [Parameter(Mandatory)][int]$DocNumber,
        [Parameter(Mandatory)][string]$TableCode,
        [Parameter(Mandatory)][int]$MonthNo,
        [Parameter(Mandatory)]$SourceRows,
        [Parameter(Mandatory)][hashtable]$ColumnMap,
        [hashtable]$TableAliases,
        [scriptblock]$LookupResolver,
        [ValidateSet('Table', 'Legacy')][string]$KeyFormat = 'Table',
        [int]$RowLimit = 0
    )
    $rowsOut = New-Object System.Collections.ArrayList
    $unmappedSet = @{}; $mappedSet = @{}; $missingSet = @{}
    $plan = @{ ProdRows = 0; Capped = 0; LookupMiss = 0; LookupUnresolved = 0; DupCells = 0; Cells = 0; MixedText = 0; MixedNumber = 0; Type3InMixed = 0 }
    $seq = 0
    foreach ($srcRow in @($SourceRows)) {
        $seq++; $plan.ProdRows++
        $cells = New-Object System.Collections.ArrayList
        $usedCols = @{}
        foreach ($col in @($srcRow.Keys)) {
            if ($col -eq '_efid' -or $col -like 'Con_*') { continue }
            $val = $srcRow[$col]
            if (Test-LandInputCellEmpty $val) { continue }
            $res = Resolve-LandRowsColumn -ProdColumn ([string]$col) -MonthNo $MonthNo -ColumnMap $ColumnMap -TableAliases $TableAliases
            if ($res.MissingTarget) { $missingSet[$res.MissingTarget] = 1; $unmappedSet[[string]$col] = 1; continue }
            $def = $res.Def
            if ($null -eq $def) { $unmappedSet[[string]$col] = 1; continue }
            $cellValue = $null; $skip = $false
            switch ($def.dataType) {
                'Lookup' {
                    if ($null -ne $LookupResolver) {
                        $cellValue = & $LookupResolver $def.reg ([string]$val)
                        if ($null -eq $cellValue) { $plan.LookupMiss++; $skip = $true }
                    }
                    else { $cellValue = 0; $plan.LookupUnresolved++ }
                }
                'Int' {
                    $num = ConvertTo-LandInputNumber -Text ([string]$val)
                    if ($null -eq $num) { $unmappedSet["$col(parse)"] = 1; $skip = $true } else { $cellValue = [int][Math]::Round($num) }
                }
                'Decimal' {
                    $num = ConvertTo-LandInputNumber -Text ([string]$val)
                    if ($null -eq $num) { $unmappedSet["$col(parse)"] = 1; $skip = $true } else { $cellValue = [double][Math]::Round([decimal]$num, 10) }
                }
                'String' {
                    $cellValue = [string]$val
                    # D-423: T9_04/T9_07 OperatingTimeReleaseOnColdVentYesNo_Mnn - String змішаного змісту (години АБО Yes/No):
                    # лічильники тексту й числа, значення не змінюються.
                    if ($def.code -like 'OperatingTimeReleaseOnColdVentYesNo*') {
                        if ($null -eq (ConvertTo-LandInputNumber -Text ([string]$val))) { $plan.MixedText++ } else { $plan.MixedNumber++ }
                    }
                }
                default { $unmappedSet["$col(type $($def.dataType))"] = 1; $skip = $true }
            }
            if ($skip) { continue }
            if ($usedCols.ContainsKey($def.code)) { $plan.DupCells++; continue }
            $usedCols[$def.code] = 1
            $mappedSet[[string]$col] = 1
            [void]$cells.Add(@{ columnCode = $def.code; value = $cellValue })
        }
        # D-423 (сторож): NAE type3 C11_10 у T9_04/T9_07 - OperatingTime тут String змішаного змісту, а type3 читає його числом
        # (Strict: арифметика над текстом). У прод-даних таких рядків немає; лічильник робить появу видимою на завантаженні.
        if ($TableCode -in 'T9_04', 'T9_07' -and (@($srcRow.Keys) -contains 'Land_NAE') -and (Test-LandNaeInType -Nae ([string]$srcRow['Land_NAE']) -Methodology 'ECW_C11_10' -Type 'type3')) { $plan.Type3InMixed++ }
        if ($cells.Count -eq 0) { continue }
        $rowKey = New-LandRowKey -DocId $DocNumber -TableCode $TableCode -Index $seq -KeyFormat $KeyFormat
        [void]$rowsOut.Add(@{ rowKey = $rowKey; baseVersion = $null; cells = $cells })
    }
    if ($RowLimit -gt 0 -and $rowsOut.Count -gt $RowLimit) {
        $plan.Capped = $rowsOut.Count - $RowLimit
        $rowsOut = New-Object System.Collections.ArrayList (, @($rowsOut.GetRange(0, $RowLimit)))
    }
    foreach ($r in $rowsOut) { $plan.Cells += $r.cells.Count }
    $plan.Rows = $rowsOut
    $plan.Unmapped = @($unmappedSet.Keys | Sort-Object)
    $plan.Mapped = @($mappedSet.Keys | Sort-Object)
    $plan.MissingTargets = @($missingSet.Keys | Sort-Object)
    return $plan
}

function Get-LandRowsSourceWithoutColumn {
    <# Template input columns no source column maps to (month suffix collapsed to _M**): counted, never invented. #>
    param([Parameter(Mandatory)][hashtable]$ColumnMap, [Parameter(Mandatory)][string[]]$FilledColumns)
    $filled = @{}
    foreach ($f in $FilledColumns) { $filled[$f] = 1 }
    $absent = @{}
    foreach ($code in $ColumnMap.Keys) {
        if (-not (Test-LandRowsInputColumn $ColumnMap[$code])) { continue }
        if ($filled.ContainsKey($code)) { continue }
        $absent[($code -replace '_M\d\d(_[A-Z]+)?$', '_M**')] = 1
    }
    return @($absent.Keys | Sort-Object)
}

function Get-LandRowsDocBlock {
    <# Document-level stop reason from the stand name state: NO_DOC | NAME_MISMATCH (unless allowed) | '' (go on). #>
    param([AllowEmptyString()][string]$NameState, [switch]$AllowNameMismatch)
    if ($NameState -eq 'NO_DOC') { return 'NO_DOC' }
    if ($NameState -eq 'NAME_MISMATCH' -and -not $AllowNameMismatch) { return 'NAME_MISMATCH' }
    return ''
}
function Get-LandRowsRejectReasons {
    <#
    .SYNOPSIS
        Із тіла HTTP 422 бере ЛИШЕ коди: messageKey і лічильник "ruleCode columnCode xN" по cells[]. Значень і rowKey не читає.
    #>
    param($Response)
    try {
        $j = $Response.Json
        if ($null -eq $j -and $Response.Text) { $j = $Response.Text | ConvertFrom-Json }
        if ($null -eq $j) { return '' }
        $counts = @{}
        foreach ($c in @($j.cells)) {
            if ($null -eq $c) { continue }
            $k = ('{0} {1}' -f [string]$c.ruleCode, [string]$c.columnCode).Trim()
            if ($counts.ContainsKey($k)) { $counts[$k]++ } else { $counts[$k] = 1 }
        }
        $parts = @($counts.Keys | Sort-Object | ForEach-Object { '{0} x{1}' -f $_, $counts[$_] })
        $mk = ''
        if ($j.PSObject.Properties.Match('messageKey').Count -gt 0) { $mk = [string]$j.messageKey }
        if ($mk) { $parts = @("messageKey=$mk") + $parts }
        return ($parts -join ', ')
    }
    catch { return '' }
}
