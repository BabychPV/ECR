<#
.SYNOPSIS
    Накладає одиниці аргументів/констант і джерела аргументів (land-script-units.json) на тексти скриптів теки convert (<код>/<версія>.ecrs).
.DESCRIPTION
    Запуск ПІСЛЯ `Ecr.MethodologyScriptMigration convert` і ПЕРЕД `shadow`. Лише локальні файли, база не чіпається.
.PARAMETER Dir      Тека результату convert (з підтеками <код методології>).
.PARAMETER Table    JSON одиниць (за замовчуванням tools\land\land-script-units.json).
#>
param([Parameter(Mandatory)][string]$Dir, [string]$Table)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'LandScriptUnits.Lib.ps1')
$unitsFile = if ($Table) { $Table } else { Join-Path $PSScriptRoot 'land-script-units.json' }
$cfg = Get-Content -LiteralPath $unitsFile -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($m in $cfg.methodologies.PSObject.Properties) {
    $sub = Join-Path $Dir $m.Name
    if (-not (Test-Path -LiteralPath $sub)) { continue }
    foreach ($f in Get-ChildItem -LiteralPath $sub -Filter '*.ecrs') {
        $args1 = @{}; if ($m.Value.arguments) { foreach ($p in $m.Value.arguments.PSObject.Properties) { $args1[$p.Name] = [string]$p.Value.unit } }
        $consts = @{}; if ($m.Value.constants) { foreach ($p in $m.Value.constants.PSObject.Properties) { $consts[$p.Name] = [string]$p.Value.unit } }
        $srcs = @{}; if ($m.Value.argumentSources) { foreach ($p in $m.Value.argumentSources.PSObject.Properties) { $srcs[$p.Name] = [string]$p.Value.alternative } }
        $text = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8)
        $new = $text
        # спершу джерела аргументів (переписують row.X), далі одиниці
        if ($srcs.Count -gt 0) { $new = Set-LandScriptArgumentSources -Text $new -Sources $srcs }
        $new = Set-LandScriptUnits -Text $new -Arguments $args1 -Constants $consts
        [IO.File]::WriteAllText($f.FullName, $new, (New-Object Text.UTF8Encoding($false)))
        Write-Host "$($m.Name)/$($f.Name): аргументів з одиницею $($args1.Count), констант $($consts.Count), джерел аргументів $($srcs.Count)"
    }
}
