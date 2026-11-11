# tools/land/LandRequiredInputs.Lib.ps1
# Land 1.0.16.0, human 10.11 + coordinator 11.11 (Q1, Q2, RI1-RI3): required inputs (MethodologyRequiredInput) per Land
# methodology. Pure data and logic, no API calls, no production values. The stage that applies it is
# `Build-LandTemplate.ps1 -Stage RequiredInputs`.
#
# Source of the list: the formulas' argument lists of every Land methodology with active rules, minus what cannot be a
# required input (derivation: .sync-local/reports/LAND-required-inputs-derived-1111.md, sent to the coordinator).
#   Input    a non-month input column the formulas read on the row itself. Empty = the step fails or gives no number.
#   RuleKey  a column of the methodology's category rule. Until the server checks rule-key columns BEFORE rule matching
#            (lane/integ-required-rulekeys, RI1-B) such a requirement is a no-op: a row with an empty key matches no rule,
#            so the gate never looks at it. Registered anyway (human 10.11 named these columns).
#   Human    named by the human 10.11 for methodologies 2-6, not read by the formulas of that methodology.
#
# Known gaps, on purpose (RI2):
#   - monthly inputs (`*_M01..M12` columns of MonthsInColumns tables): the gate checks a row, not a month; an empty month
#     is normal for a month that has not come yet;
#   - cross-row inputs read through bridges (GTU components for ECW_C12_02, composition by stream): the value is on
#     another row or table;
#   - ECW_C11_11_04 mass fractions on T9a_19 (Benzene, Xylene, Toluene, SaturatedHydrocarbonsC1C5, C6C10): one fraction
#     per substance, a row without a fraction legitimately gives no number for that substance - waits for AN's word;
#   - methodologies without active rules (ECW_C11_15, ECW_C09_02_01) and without row inputs of their own.
#
# Column ids are stored as given and translated by sheet/table/column PATH when a document is checked
# (MethodologyKeyLocalizer), so the id of ANY Land template version works; the stage takes the latest published one and
# compares what is already registered by path, not by id.

$script:LandRequiredInputs = [ordered]@{
    # C05: the human's four columns on T3_00 for methodologies 2-6, plus what the formulas read.
    'ECW_C05_01'    = @(
        @{ Table = 'T3_00'; Kind = 'RuleKey'; Columns = @('Land_TypeEquipment', 'Land_TypeFuel') }
        @{ Table = 'T3_00'; Kind = 'Input'; Columns = @('Land_RotationRef', 'Land_Power', 'Land_OverhauledEnginesRef') }
        @{ Table = 'T3_00'; Kind = 'Human'; Columns = @('Land_DescriptionEquipment') }
    )
    'ECW_C05_02'    = @(
        @{ Table = 'T3_00'; Kind = 'RuleKey'; Columns = @('Land_TypeEquipment', 'Land_TypeFuel') }
        @{ Table = 'T3_00'; Kind = 'Human'; Columns = @('Land_RotationRef', 'Land_DescriptionEquipment') }
    )
    'ECW_C05_03'    = @(
        @{ Table = 'T3_00'; Kind = 'RuleKey'; Columns = @('Land_TypeEquipment', 'Land_TypeFuel') }
        @{ Table = 'T3_00'; Kind = 'Human'; Columns = @('Land_RotationRef', 'Land_DescriptionEquipment') }
    )
    'ECW_C05_04'    = @(
        @{ Table = 'T3_00'; Kind = 'RuleKey'; Columns = @('Land_TypeEquipment', 'Land_TypeFuel') }
        @{ Table = 'T3_00'; Kind = 'Human'; Columns = @('Land_RotationRef', 'Land_DescriptionEquipment') }
        @{ Table = 'T3_00'; Kind = 'Input'; Columns = @('Land_EF_KNO2') }
    )
    'ECW_C05_05'    = @(
        @{ Table = 'T3_00'; Kind = 'RuleKey'; Columns = @('Land_TypeEquipment', 'Land_TypeFuel') }
        @{ Table = 'T3_00'; Kind = 'Human'; Columns = @('Land_RotationRef', 'Land_DescriptionEquipment') }
        @{ Table = 'T3_00'; Kind = 'Input'; Columns = @('Land_EF_KNO2') }
    )
    'ECW_C07_07'    = @(
        @{ Table = 'T5_08'; Kind = 'Input'; Columns = @('Land_ERG_ExpFactor', 'Land_ERG_MaximumVolume', 'Land_ERG_StorEmissions') }
    )
    'ECW_C07_07_01' = @(
        @{ Table = 'T5_08'; Kind = 'Input'; Columns = @('Land_ERG_ExpFactor', 'Land_ERG_MaximumVolume') }
    )
    'ECW_C07_08'    = @(
        @{ Table = 'T5_09'; Kind = 'Input'; Columns = @('Land_ERG_DurationAVGdischarge', 'Land_ERG_Vsl') }
    )
    'ECW_C07_21'    = @(
        @{ Table = 'T5_27'; Kind = 'Input'; Columns = @('Con_Region') }
    )
    'ECW_C08_04_03' = @(
        @{ Table = 'T6_12'; Kind = 'Input'; Columns = @(
                'B1FactorTakingIntoAccountLoadingHeig', 'DustTrappingEfficiency', 'K1ShareOfDustFractionInTheMaterial',
                'K2ShareOfVolatileDustTransformingInA', 'K3AvrFactorTakingIntoAccountLocalMet', 'K3MaxFactorTakingIntoAccountLocalMet',
                'K4FactorTakingIntoAccountLocalCondit', 'K5FactorTakingIntoAccountMoistureOfM', 'K7FactorTakingIntoAccountCoarsenessO',
                'K8CorrectionFactorForDifferentMateri', 'K9CorrectionFactorUnderForcefulSalvo') }
    )
    # C11: fuel type and stream select the composition; power is read by C11_03.
    'ECW_C11_01'    = @(
        @{ Table = 'T9_04'; Kind = 'Input'; Columns = @('TypeOfFuel') }
        @{ Table = 'T9a_16'; Kind = 'Input'; Columns = @('Stream') }
        @{ Table = 'T9a_17'; Kind = 'Input'; Columns = @('Stream') }
        @{ Table = 'T9a_27'; Kind = 'Input'; Columns = @('Stream') }
        @{ Table = 'T9a_30'; Kind = 'Input'; Columns = @('Stream') }
    )
    'ECW_C11_02'    = @(
        @{ Table = 'T9_04'; Kind = 'Input'; Columns = @('TypeOfFuel') }
        @{ Table = 'T9a_16'; Kind = 'Input'; Columns = @('Stream') }
        @{ Table = 'T9a_27'; Kind = 'Input'; Columns = @('Stream') }
    )
    'ECW_C11_03'    = @(
        @{ Table = 'T9_03'; Kind = 'Input'; Columns = @('PowerKW') }
        @{ Table = 'T9_05'; Kind = 'Input'; Columns = @('PowerKW') }
        @{ Table = 'T9_06'; Kind = 'Input'; Columns = @('PowerKW') }
        @{ Table = 'T9_09'; Kind = 'Input'; Columns = @('PowerKW') }
        @{ Table = 'T9_13'; Kind = 'Input'; Columns = @('PowerKW') }
        @{ Table = 'T9_14'; Kind = 'Input'; Columns = @('PowerKW') }
    )
    'ECW_C11_04'    = @(
        @{ Table = 'T9_04'; Kind = 'Input'; Columns = @('TypeOfFuel') }
    )
    'ECW_C11_05'    = @(
        @{ Table = 'T9_07'; Kind = 'Input'; Columns = @('TypeOfFuel') }
    )
    'ECW_C11_06'    = @(
        @{ Table = 'T9a_25'; Kind = 'Input'; Columns = @('Stream') }
    )
    'ECW_C11_08'    = @(
        @{ Table = 'T9_08'; Kind = 'Input'; Columns = @('TypeOfFuel') }
        @{ Table = 'T9_10'; Kind = 'Input'; Columns = @('TypeOfFuel') }
        @{ Table = 'T9a_17'; Kind = 'Input'; Columns = @('Stream') }
    )
    'ECW_C11_10'    = @(
        @{ Table = 'T9_02'; Kind = 'Input'; Columns = @('TypeOfFuel') }
        @{ Table = 'T9_04'; Kind = 'Input'; Columns = @('TypeOfFuel') }
        @{ Table = 'T9_07'; Kind = 'Input'; Columns = @('TypeOfFuel') }
        @{ Table = 'T9_11'; Kind = 'Input'; Columns = @('TypeOfFuel') }
        @{ Table = 'T9a_24'; Kind = 'Input'; Columns = @('Stream') }
        @{ Table = 'T9a_25'; Kind = 'Input'; Columns = @('Stream') }
        @{ Table = 'T9a_26'; Kind = 'Input'; Columns = @('Stream') }
        @{ Table = 'T9a_27'; Kind = 'Input'; Columns = @('Stream') }
        @{ Table = 'T9a_28'; Kind = 'Input'; Columns = @('Stream') }
    )
    # C12: streams and equipment counts (fugitive), fuel type, NAE code, tank factors, power.
    'ECW_C12_01'    = @(
        @{ Table = 'T11_02'; Kind = 'Input'; Columns = @('Streams', 'FlangesNo', 'PumpNo', 'FB_DMSPumpNo', 'SafValvesNo', 'ValvesNo') }
        @{ Table = 'T11a_01'; Kind = 'Input'; Columns = @('Streams', 'FlangesNo', 'PumpNo', 'FB_DMSPumpNo', 'SafValvesNo', 'ValvesNo') }
        @{ Table = 'T11b_01'; Kind = 'Input'; Columns = @('Streams', 'FlangesNo', 'PumpNo', 'FB_DMSPumpNo', 'SafValvesNo', 'ValvesNo') }
        @{ Table = 'T11c_01'; Kind = 'Input'; Columns = @('Streams', 'FB_FlangesNo', 'FB_PumpNo', 'FB_DMSPumpNo', 'FB_SafValvesNo', 'FB_ValvesNo') }
        @{ Table = 'T11d_01'; Kind = 'Input'; Columns = @('Streams', 'FB_FlangesNo', 'FB_PumpNo', 'FB_DMSPumpNo', 'FB_SafValvesNo', 'FB_ValvesNo') }
    )
    'ECW_C12_02'    = @(
        @{ Table = 'T10_03'; Kind = 'Input'; Columns = @('IDCodeNAEProject', 'Land_TypeFuel') }
        @{ Table = 'T11_03'; Kind = 'Input'; Columns = @('IDCodeNAEProject', 'TypeOfStreams') }
    )
    'ECW_C12_03'    = @(
        @{ Table = 'T10_01'; Kind = 'Input'; Columns = @('IDCodeNAEProject', 'Land_TypeFuel') }
        @{ Table = 'T11_01'; Kind = 'Input'; Columns = @('IDCodeNAEProject', 'Land_TypeFuel') }
    )
    'ECW_C12_04_02' = @(
        @{ Table = 'T10_05'; Kind = 'Input'; Columns = @('TypeOfStoredMaterial', 'GxpStorageEmissionsOfHCVapourFromOne', 'KpMaxExperimentalFactor', 'VMaxTheMaximumVolumeOfTheSteamAirMix') }
        @{ Table = 'T10a_02'; Kind = 'Input'; Columns = @('TypeOfStoredMaterial', 'GxpStorageEmissionsOfHCVapourFromOne', 'KpMaxExperimentalFactor', 'VMaxTheMaximumVolumeOfTheSteamAirMix') }
        @{ Table = 'T11_04'; Kind = 'Input'; Columns = @('TypeOfStoredMaterial', 'GxpStorageEmissionsOfHCVapourFromOne', 'KpMaxExperimentalFactor', 'VMaxTheMaximumVolumeOfTheSteamAirMix') }
    )
    'ECW_C12_05'    = @(
        @{ Table = 'T10_04'; Kind = 'Input'; Columns = @('IDCodeNAEProject', 'PowerKW') }
        @{ Table = 'T10a_01'; Kind = 'Input'; Columns = @('IDCodeNAEProject', 'PowerKW') }
    )
}

# Methodology codes that have required inputs, in the order of the list.
function Get-LandRequiredInputMethodologies {
    return @($script:LandRequiredInputs.Keys)
}

# Flat list for one methodology: Methodology, Table (short table code), Column, Kind, Path ("TABLE/COLUMN").
# A column listed twice for a table (e.g. as RuleKey and Input) comes once, with the first kind.
function Get-LandRequiredInputs {
    param([Parameter(Mandatory)][string]$Methodology)
    if (-not $script:LandRequiredInputs.Contains($Methodology)) { return @() }
    $seen = @{}
    $out = @()
    foreach ($g in @($script:LandRequiredInputs[$Methodology])) {
        foreach ($c in @($g.Columns)) {
            $path = "$($g.Table)/$c"
            if ($seen.ContainsKey($path)) { continue }
            $seen[$path] = $true
            $out += [pscustomobject]@{ Methodology = $Methodology; Table = [string]$g.Table; Column = [string]$c; Kind = [string]$g.Kind; Path = $path }
        }
    }
    return $out
}

# Next version number: max over the numbers of the form V<n> + 1. Version names are not always V<n> on a stand
# (a version named «test» exists on live), so the number of the LAST version cannot be parsed.
function Get-LandRequiredInputNextVersion {
    param([object[]]$Versions = @())
    $max = 0
    foreach ($v in @($Versions)) {
        if ([string]$v.versionNumber -match '^[Vv](\d+)$') { $n = [int]$Matches[1]; if ($n -gt $max) { $max = $n } }
    }
    return 'V' + ($max + 1)
}

# Where the requirements go.
#   Draft    the draft to write into: the newest draft named V<n>. A draft with another name is somebody's own work
#            (Foreign) and is never touched.
#   Base     the newest published version: what a new draft is copied from, and what "already registered" is read from
#            when there is no draft.
#   Next     the number for a new draft.
function Get-LandRequiredInputTarget {
    param([object[]]$Versions = @())
    $all = @($Versions | Where-Object { $_ } | Sort-Object { [int]$_.id } -Descending)
    $drafts = @($all | Where-Object { [string]$_.status -eq 'Draft' })
    $own = @($drafts | Where-Object { [string]$_.versionNumber -match '^[Vv]\d+$' }) | Select-Object -First 1
    $foreign = @($drafts | Where-Object { [string]$_.versionNumber -notmatch '^[Vv]\d+$' })
    $base = @($all | Where-Object { [string]$_.status -eq 'Published' }) | Select-Object -First 1
    return [pscustomobject]@{ Draft = $own; Base = $base; Foreign = $foreign; Next = (Get-LandRequiredInputNextVersion $all) }
}

# What to PUT: wanted paths that are not registered, or registered with another severity.
#   Wanted   objects with Path.
#   Current  objects with Path and Severity (registered now; a path the template no longer has is $null and ignored).
function Get-LandRequiredInputDiff {
    param([object[]]$Wanted = @(), [object[]]$Current = @(), [Parameter(Mandatory)][ValidateSet('Warn', 'Block')][string]$Severity)
    $have = @{}
    foreach ($c in @($Current)) { if ($c -and $c.Path) { $have[[string]$c.Path] = [string]$c.Severity } }
    $todo = @()
    foreach ($w in @($Wanted)) {
        # the dictionary key is case-insensitive in PowerShell; severity is compared exactly as the server names it
        if ($have.ContainsKey([string]$w.Path) -and $have[[string]$w.Path] -ceq $Severity) { continue }
        $todo += $w
    }
    return $todo
}
