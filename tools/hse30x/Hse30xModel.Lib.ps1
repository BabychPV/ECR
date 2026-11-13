# tools/hse30x/Hse30xModel.Lib.ps1
# Template model HSE30X (FLERT F4b-1/F4b-2, design .sync-local/reports/FLERT-F4b-design-1112.md §1-§2):
# SG_FLARING (sour-gas flaring, VARIANT INT/CONT_ON/CONT_OFF) and TOX_SG (Thermaloxidizer, file import).
# Shared by Build-Hse30xTemplate.ps1 and Test-Hse30xGuards.ps1. No network, no stand.
#
# Code wins over the design (src/Ecr.Expressions, FunctionRegistry.TemplateSet):
#  - CONTAINS and MONTH do not exist in the TEMPLATE dialect (FlertCaseLinkTests: "CONTAINS ... лише методологія"),
#    so case links match CASE_NAME exactly against [FlertCaseName], and the CONT_OFF season reads [Period].Sequence
#    (monthly documents, period = month of Start, Q3) instead of MONTH([Start]).
#  - CellDataType has no Enum/DateTime: enums are String + allowed Values, Start/End are Date.
# Assumption [A]: monthly documents (design §6 default), periodKind = Monthly.

Set-StrictMode -Version 2.0

$script:H30TemplateCode = 'HSE30X'
$script:H30SheetCode = 'HSE30X'
$script:H30PeriodKind = 'Monthly'   # [A] design §6 default; Q3: period = month of the event start
$script:H30SchemaFile = Join-Path $PSScriptRoot '../../src/Ecr.Application/Registries/Flert/FlertCompositionSchema.cs'   # forward slashes: CI runs pwsh on Linux
$script:H30ToxLegacyZero = @('Helium', 'C2H6S')   # legacy TOx forces 0 (R_Air_HSE_Thermaloxidizer_General.cs:364-365)
$script:H30Season = "IF([Period].Sequence >= 4 && [Period].Sequence <= 9, 'Summer', 'Winter')"   # Q5/Q11, CONT_OFF only

function New-H30Col {
    param([string]$Code, [string]$Type, [string]$En, [bool]$Hidden = $false, [string]$Expr = $null,
        [string[]]$Values = $null, [string]$Source = $null, [string]$Default = $null, [string]$ResultRegistry = $null)
    $kind = if ($Expr) { 'Formula' } else { 'Input' }
    return [pscustomobject][ordered]@{ code = $Code; dataType = $(if ($Expr) { 'Formula' } else { $Type }); valueType = $Type; kind = $kind
        headerEn = $En; hidden = $Hidden; expression = $(if ($Expr) { $Expr } else { $null }); values = $Values; source = $Source
        defaultValue = $(if ($Default) { $Default } else { $null }); resultRegistry = $(if ($ResultRegistry) { $ResultRegistry } else { $null }) }
}

function Get-H30ComponentColumns {
    <# (Column, Component) pairs of FlertCompositionSchema.ComponentColumns, read from the C# source (single source of truth). #>
    param([string]$SchemaFile = $script:H30SchemaFile)
    $text = [IO.File]::ReadAllText($SchemaFile)
    $m = [regex]::Match($text, 'ComponentColumns\s*=\s*\[(?<body>.*?)\];', 'Singleline')
    if (-not $m.Success) { throw "ComponentColumns not found in $SchemaFile" }
    $pairs = [regex]::Matches($m.Groups['body'].Value, '\("(?<c>\w+)",\s*"(?<k>\w+)"\)')
    return , @($pairs | ForEach-Object { [pscustomobject]@{ Column = $_.Groups['c'].Value; Component = $_.Groups['k'].Value } })
}

function Get-H30GcvExpr {
    param([string]$Component)
    return "REGSUM('GAS_COMPOSITION', ROW.CASE = [FlertCase] && ROW.COMPONENT = REGFIND('COMPONENT', '$Component'), ROW.MOL_PCT)"
}

function Get-H30CaseLink {
    <# REGONE text per path. INT = exactly the formula FlertCaseLinkTests evaluates. #>
    param([ValidateSet('INT', 'CONT_ON', 'CONT_OFF', 'TOX')][string]$Variant)
    switch ($Variant) {
        'INT' { "REGONE('STREAM_CASE', ROW.STREAM.LEGACY_ID = [FlertStreamId] && ROW.CASE_NAME = [FlertCaseName])" }
        'CONT_ON' { "REGONE('STREAM_CASE', ROW.STREAM.NUMBER = '416' && ROW.STREAM.IS_ONSHORE = TRUE && ROW.CASE_NAME = [FlertCaseName])" }
        'CONT_OFF' { "REGONE('STREAM_CASE', ROW.STREAM.LEGACY_ID = [FlertStreamId] && ROW.CASE_NAME = [FlertCaseName])" }
        'TOX' { "REGONE('STREAM_CASE', ROW.STREAM.NUMBER = [StreamNumber] && ROW.STREAM.IS_ONSHORE = TRUE && ROW.CASE_NAME = [FlertCaseName])" }
    }
}

function Add-H30Gcv {
    param($List, [switch]$Tox)
    foreach ($p in (Get-H30ComponentColumns)) {
        $List.Add((New-H30Col "GCV_$($p.Column)" Decimal "GCV $($p.Column), mol% (FLERT $($p.Component))" -Hidden $true -Expr (Get-H30GcvExpr $p.Component)))
    }
    if ($Tox) { foreach ($z in $script:H30ToxLegacyZero) { $List.Add((New-H30Col "GCV_$z" Decimal "GCV $z (legacy TOx: always 0)" -Hidden $true -Expr '0')) } }
}

function Get-H30SgColumns {
    $c = New-Object System.Collections.Generic.List[object]
    $c.Add((New-H30Col VARIANT String 'Variant' -Values @('INT', 'CONT_ON', 'CONT_OFF') -Source 'SourceEventMap default (Q8)'))
    $c.Add((New-H30Col SourceEventId String 'EF id' -Hidden $true -Source 'EF ID (EFID)'))
    $c.Add((New-H30Col Start Date 'Start' -Source 'EF StartTime'))
    $c.Add((New-H30Col End Date 'End' -Source 'EF EndTime'))
    $c.Add((New-H30Col Duration Int 'Duration, s' -Source 'EF attribute / DATEDIFF'))
    $c.Add((New-H30Col Value Decimal 'Volume, m3' -Source 'INT: AdjustedV; CONT: *_LP_Sour_Analyzers'))
    $c.Add((New-H30Col Coefficient Decimal 'Coefficient' -Default '1' -Source 'CONT_OFF: map constant (Q10); otherwise 1'))
    $c.Add((New-H30Col Total Decimal 'Total, m3' -Expr '[Value] * [Coefficient]'))
    $c.Add((New-H30Col Location String 'Location' -Values @('ONSHORE', 'A_ISLAND', 'D_ISLAND') -Source 'EF Location via ValueMap (Q13); unmatched -> Unmapped'))
    foreach ($n in 'Area', 'Unit', 'Equipment', 'TagNumber', 'FlareUnit', 'Category') { $c.Add((New-H30Col $n String $n -Source 'EF, Direct')) }
    foreach ($n in 'IsAuto', 'IsOnshore', 'IsHMB') { $c.Add((New-H30Col $n Bool $n -Hidden $true -Source 'EF, Direct')) }
    $c.Add((New-H30Col FlertStreamId Int 'FLERT stream id' -Hidden $true -Source 'INT: EF GasCompositionID; CONT_ON: 317; CONT_OFF: map constant 19/21/146'))
    $c.Add((New-H30Col FlertCaseText String 'FLERT case text' -Hidden $true -Source 'INT: EF StreamType; CONT_ON: map constant (exact SOR case name)'))
    $c.Add((New-H30Col FlertCaseName String 'FLERT case name' -Hidden $true -Expr "IF([VARIANT] = 'CONT_OFF', $($script:H30Season), [FlertCaseText])"))
    $link = "IF([VARIANT] = 'INT', $(Get-H30CaseLink INT), IF([VARIANT] = 'CONT_ON', $(Get-H30CaseLink CONT_ON), " +
        "IF([VARIANT] = 'CONT_OFF', $(Get-H30CaseLink CONT_OFF), REGONE('STREAM_CASE', FALSE))))"
    $c.Add((New-H30Col FlertCase Int 'FLERT case' -Hidden $true -Expr $link -ResultRegistry 'STREAM_CASE'))
    $c.Add((New-H30Col FlertNoComposition Bool 'FLERT: no composition' -Hidden $true -Expr "REGFIELD([FlertCase], 'NO_COMPOSITION')"))
    Add-H30Gcv $c
    return , $c.ToArray()
}

function Get-H30ToxColumns {
    $c = New-Object System.Collections.Generic.List[object]
    $c.Add((New-H30Col Date Date 'Date' -Source 'Excel row (strict parse; junk -> import error)'))
    $c.Add((New-H30Col Train String 'Train' -Values @('Train 1', 'Train 2') -Source 'Excel'))
    $c.Add((New-H30Col Mode String 'Mode' -Values @('Normal', 'By-pass') -Source 'Excel'))
    $c.Add((New-H30Col Type String 'Type' -Values @('SOR', 'EOR') -Source 'Excel (EOR empty, Q12)'))
    $c.Add((New-H30Col StreamNumber String 'Stream number' -Source 'Excel column -> unpivot'))
    $c.Add((New-H30Col Volume Decimal 'Volume' -Source 'Excel G<n>_Volume'))
    $c.Add((New-H30Col OperTime Decimal 'Operating time' -Source 'Excel G<n>_OperTime'))
    foreach ($n in 'MethodCalculation', 'SourceFile', 'SourceSheet') { $c.Add((New-H30Col $n String $n -Hidden $true -Source 'Excel / file name')) }
    $c.Add((New-H30Col FlertCaseName String 'FLERT case name' -Hidden $true -Source 'TOx loader: exact CASE_NAME of the stream SOR/EOR case'))
    $c.Add((New-H30Col FlertCase Int 'FLERT case' -Hidden $true -Expr (Get-H30CaseLink TOX) -ResultRegistry 'STREAM_CASE'))
    $c.Add((New-H30Col FlertNoComposition Bool 'FLERT: no composition' -Hidden $true -Expr "REGFIELD([FlertCase], 'NO_COMPOSITION')"))
    Add-H30Gcv $c -Tox
    return , $c.ToArray()
}

function Get-H30Model {
    return [ordered]@{
        templateCode = $script:H30TemplateCode; periodKind = $script:H30PeriodKind; sheet = $script:H30SheetCode
        design = '.sync-local/reports/FLERT-F4b-design-1112.md'
        tables = @(
            [ordered]@{ code = 'SG_FLARING'; rowMode = 'Dynamic'; columns = (Get-H30SgColumns) },
            [ordered]@{ code = 'TOX_SG'; rowMode = 'Dynamic'; columns = (Get-H30ToxColumns) })
    }
}
