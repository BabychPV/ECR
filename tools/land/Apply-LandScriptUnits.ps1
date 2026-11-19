<#
.SYNOPSIS
    Накладає одиниці аргументів/констант (land-script-units.json) на тексти скриптів теки convert (<код>/<версія>.ecrs).
.DESCRIPTION
    Запуск ПІСЛЯ `Ecr.MethodologyScriptMigration convert` і ПЕРЕД `shadow`. Лише локальні файли, база не чіпається.
.PARAMETER Dir      Тека результату convert (з підтеками <код методології>).
.PARAMETER Table    JSON одиниць (за замовчуванням tools\land\land-script-units.json).
#>
param([Parameter(Mandatory)][string]$Dir, [string]$Table)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LandScriptUnits.Lib.ps1')
if (-not $Table) { $Table = Join-Path $PSScriptRoot 'land-script-units.json' }
$cfg = Get-Content -LiteralPath $Table -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($m in $cfg.methodologies.PSObject.Properties) {
    $sub = Join-Path $Dir $m.Name
    if (-not (Test-Path -LiteralPath $sub)) { continue }
    foreach ($f in Get-ChildItem -LiteralPath $sub -Filter '*.ecrs') {
        $args1 = @{}; if ($m.Value.arguments) { foreach ($p in $m.Value.arguments.PSObject.Properties) { $args1[$p.Name] = [string]$p.Value.unit } }
        $consts = @{}; if ($m.Value.constants) { foreach ($p in $m.Value.constants.PSObject.Properties) { $consts[$p.Name] = [string]$p.Value.unit } }
        $text = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
        $new = Set-LandScriptUnits -Text $text -Arguments $args1 -Constants $consts
        [IO.File]::WriteAllText($f.FullName, $new, (New-Object Text.UTF8Encoding($false)))
        Write-Host "$($m.Name)/$($f.Name): аргументів $($args1.Count), констант $($consts.Count)"
    }
}
