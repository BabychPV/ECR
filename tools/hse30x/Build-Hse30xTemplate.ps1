<#
.SYNOPSIS
    HSE30X (FLERT F4b): builds the template model (SG_FLARING + TOX_SG) as JSON and prints the plan. PREVIEW ONLY:
    no stand, no HTTP, nothing is published - publishing goes in the live window after all fixes (design §7).
    Re-run is idempotent: same model -> same bytes -> "0 changes".
.PARAMETER OutDir
    Where hse30x-model.json is written (default: tools/hse30x/out, git-ignored).
#>
[CmdletBinding()]
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'Hse30xModel.Lib.ps1')
if (-not $OutDir) { $OutDir = Join-Path $here 'out' }
if (-not (Test-Path $OutDir)) { $null = New-Item -ItemType Directory -Path $OutDir }
$file = Join-Path $OutDir 'hse30x-model.json'

$model = Get-H30Model
$json = (ConvertTo-Json -InputObject $model -Depth 8) -replace "`r`n", "`n"

$old = @{}
if (Test-Path $file) {
    $prev = [IO.File]::ReadAllText($file) | ConvertFrom-Json
    foreach ($t in @($prev.tables)) { foreach ($col in @($t.columns)) { $old["$($t.code).$($col.code)"] = (ConvertTo-Json -InputObject $col -Depth 4 -Compress) } }
}
$changes = 0
foreach ($t in $model.tables) {
    $f = @($t.columns | Where-Object { $_.kind -eq 'Formula' }).Count
    $h = @($t.columns | Where-Object { $_.hidden }).Count
    Write-Host ("[preview] {0}.{1}: columns {2}, formulas {3}, hidden {4}, GCV {5}" -f $model.templateCode, $t.code, $t.columns.Count, $f, $h,
        @($t.columns | Where-Object { $_.code -like 'GCV_*' }).Count)
    foreach ($col in $t.columns) {
        $k = "$($t.code).$($col.code)"
        if (-not $old.ContainsKey($k) -or $old[$k] -ne (ConvertTo-Json -InputObject $col -Depth 4 -Compress)) { $changes++ }
        $old.Remove($k)
    }
}
$changes += $old.Count   # columns that disappeared from the model

$same = (Test-Path $file) -and ([IO.File]::ReadAllText($file) -ceq $json)
if (-not $same) { [IO.File]::WriteAllText($file, $json, (New-Object Text.UTF8Encoding($false))) }
Write-Host ("[preview] {0} -> {1}; changes: {2}" -f $model.templateCode, $file, $(if ($same) { 0 } else { [Math]::Max($changes, 1) }))
