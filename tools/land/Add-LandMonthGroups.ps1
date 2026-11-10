<#
.SYNOPSIS
    Місячні смуги (групи заголовків «January…December» + батьківські смуги з xlsm) для версії шаблону Land через публічний API.
    Презентаційна правка (ФВ-7.2): працює й на ОПУБЛІКОВАНІЙ версії, значень не чіпає, піднімає PresentationRevision.

.DESCRIPTION
    На кожну таблицю з колонками, прив'язаними до місяця (IsMonthColumn, MonthNumber 1..12): група на кожен СУМІЖНИЙ блок
    колонок одного місяця (другий блок того ж місяця — друга група з тим самим підписом), батьківські смуги (квартал/розділ)
    з tools/land/land-month-bands.json, не глибше 3 рівнів. На таблицю — ОДИН PUT …/tables/{id}/header-groups і ОДИН
    PATCH …/presentation (HeaderGroupId), і лише коли щось змінюється: повторний запуск дає 0 змін.
    Таблиці без місячних колонок не чіпаються. Чужі групи (код не MB_) зберігаються; місячна колонка в чужій групі —
    помилка, таблицю пропущено. Дублі Ordinal звітуються (сервер упорядковує їх за Code), нічого не переставляється.
    Обліковий запис — -User/-Password або ECR_USER/ECR_PASSWORD (EcrApi.ps1); мутації — лише з -ConfirmStand.

.EXAMPLE
    # план (лише GET):
    powershell -File tools\land\Add-LandMonthGroups.ps1 -BaseUrl http://localhost:5094 -TemplateCode LAND_UI -Version 1.0.14.0 -WhatIf
    # застосування:
    powershell -File tools\land\Add-LandMonthGroups.ps1 -BaseUrl http://localhost:5094 -TemplateCode LAND_UI -Version 1.0.14.0 -ConfirmStand localhost:5094
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = $(if ($env:ECR_BASE_URL) { $env:ECR_BASE_URL } else { 'http://localhost:5094' }),
    [string]$TemplateCode = 'LAND_UI',
    [Parameter(Mandatory)][string]$Version,
    [string]$BandSource,
    [string]$User,
    [securestring]$Password,
    [string]$ConfirmStand,
    [string[]]$Tables,
    [switch]$WhatIf
)
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandMonthBands.Lib.ps1')
Set-StrictMode -Off
$srcPath = if ($BandSource) { $BandSource } else { Join-Path $here 'land-month-bands.json' }
$src = Read-LandMonthBandSource $srcPath
function Log([string]$m) { Write-Host ('[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $m) }

Assert-LocalBaseUrl $BaseUrl
if (-not $WhatIf) { Set-EcrStandConfirmation -Stand $ConfirmStand -Url $BaseUrl }
Connect-Ecr -BaseUrl $BaseUrl -User $User -Password $Password

$t = Invoke-Ecr -Method GET -Path ('/api/v1/templates?limit=50&q=' + [uri]::EscapeDataString($TemplateCode))
Assert-EcrOk $t 'GET templates'
$tpl = @($t.Json.items) | Where-Object { $_.code -eq $TemplateCode } | Select-Object -First 1
if ($null -eq $tpl) { throw "Шаблону $TemplateCode немає." }
$vs = Invoke-Ecr -Method GET -Path "/api/v1/templates/$($tpl.id)/versions?limit=100"
Assert-EcrOk $vs 'GET versions'
$ver = @($vs.Json.items) | Where-Object { [string]$_.version -eq $Version } | Select-Object -First 1
if ($null -eq $ver) { throw "Версії $Version шаблону $TemplateCode немає." }
Log ("{0} {1} (id {2}, {3}){4}" -f $TemplateCode, $Version, $ver.id, $ver.status, $(if ($WhatIf) { ' — ПЛАН, без змін' } else { '' }))

$sum = Invoke-LandMonthBands -VersionId ([int]$ver.id) -Source $src -WhatIfOnly:$WhatIf -OnlyTables $Tables -Log { param($m) Log $m }
foreach ($d in $sum.DuplicateOrdinals) { Log "  дубль Ordinal: $d" }
Log ("Підсумок: таблиць {0}, з місячними колонками {1}, {2} {3}, пропущено з помилкою {4}; місячних груп {5}, батьківських {6}, колонок у групах {7}; змін колонок {8}; PUT {9}, PATCH {10}; дублів Ordinal {11}" -f `
        $sum.Tables, $sum.TablesWithMonths, $(if ($WhatIf) { 'змінилося б' } else { 'змінено' }), $sum.TablesChanged, $sum.TablesSkippedErrors,
    $sum.MonthGroups, $sum.ParentGroups, $sum.AssignedColumns, $sum.ColumnChanges, $sum.Puts, $sum.Patches, @($sum.DuplicateOrdinals).Count)
if ($sum.TablesSkippedErrors -gt 0) { exit 2 }
