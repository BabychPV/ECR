<#
.SYNOPSIS
    Переносить значення зі застарілих таблиць Land T9a_03 / T9a_06 / T9a_07 у T9a_17a / T9a_21a / T9a_21b (D-1).

.DESCRIPTION
    Нова версія шаблону публікується БЕЗ -PruneStaleTables, migrate-version іде у режимі Safe без мапи
    перейменувань, тож старі таблиці лишаються в документах разом зі значеннями. Цей скрипт - окремий крок ПІСЛЯ
    міграції: читає зрізи старих таблиць і через PATCH /api/v1/documents/{id}/cells пише значення в нові.

    Відповідність (tools/land/rename-maps/land-9a-missing-tables.json, або -MapPath):
      - таблиця: fromTableCode -> toTableCode (на тому самому аркуші);
      - рядок: rowKey зберігається (так само, як у migrate-version);
      - колонка: за кодом колонки; запис мапи може мати columns[{fromColumnCode,toColumnCode}].
    Значення, яким немає куди йти (колонки немає в новій таблиці, колонка лише для читання, рядка немає в таблиці
    без динамічних рядків), рахуються окремо й НЕ записуються. Цільова комірка з іншим значенням - конфлікт:
    пропуск, без -OverwriteConflicts не перезаписується. Повтор після успіху дає 0 змін.

    За замовчуванням скрипт ЛИШЕ читає й друкує план (лічильники, значення не друкуються). Запис - тільки з
    -Execute і -ConfirmStand. Джерело не змінюється, якщо немає -ClearSource.

    ⚠ Аркуш у стані Submitted/Approved для запису треба спершу відкрити (Reopen). Цей скрипт Reopen НЕ виконує:
    це робить людина/координатор. Якщо є що писати, а аркуш не відкритий, -Execute зупиняється ДО будь-якого
    запису з повідомленням «потрібен Reopen аркуша X/період Y».

    Лише локальний стенд (localhost, 127.0.0.1, [::1]); пейсинг не частіше 1 виклику на 2.2 с, Retry-After поважається.

.PARAMETER ApiBase          Адреса API стенда (за замовчуванням $env:ECR_BASE_URL або http://localhost:5092).
.PARAMETER ConfirmStand     Явне підтвердження стенда для запису: адреса API (повна, host:port або host).
.PARAMETER ProjectId        Проєкт: усі його документи (GET /documents?projectId=).
.PARAMETER DocumentId       Конкретні документи (3,10 або 3;10).
.PARAMETER PeriodKey        Періоди Рік*100+Місяць (202606 або 202603,202606).
.PARAMETER Year             Якщо PeriodKey не задано: усі 12 місяців цього року.
.PARAMETER Execute          Виконати запис (без нього - лише план).
.PARAMETER OverwriteConflicts  Перезаписувати цільові комірки з іншим значенням.
.PARAMETER ClearSource      Після переносу стирати значення в старій таблиці (за замовчуванням ні).

.EXAMPLE
    $pw = Read-Host 'Пароль ECR' -AsSecureString
    .\Move-LandStaleTableValues.ps1 -ApiBase http://localhost:5094 -User admin -Password $pw -DocumentId 2 -PeriodKey 202606
    .\Move-LandStaleTableValues.ps1 -ApiBase http://localhost:5094 -User admin -Password $pw -DocumentId 2 -PeriodKey 202606 -Execute -ConfirmStand localhost:5094
#>
[CmdletBinding()]
param(
    [Alias('BaseUrl')]
    [string]$ApiBase = $(if ($env:ECR_BASE_URL) { $env:ECR_BASE_URL } else { 'http://localhost:5092' }),
    [string]$User,
    [securestring]$Password,
    [string]$ConfirmStand,
    [int]$ProjectId,
    $DocumentId,
    $PeriodKey,
    [int]$Year,
    [string]$MapPath,
    [switch]$DryRun,
    [switch]$Execute,
    [switch]$OverwriteConflicts,
    [switch]$ClearSource,
    [double]$PaceSeconds = 2.2
)

$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
. (Join-Path $here 'EcrApi.ps1')
. (Join-Path $here 'LandStaleValues.Lib.ps1')

# Лише локальний стенд - ДО будь-якого виклику.
Assert-LocalBaseUrl $ApiBase

$exec = ($Execute.IsPresent -and -not $DryRun.IsPresent)
if ($exec) { Set-EcrStandConfirmation -Stand $ConfirmStand -Url $ApiBase }

$mapFile = $MapPath
if (-not $mapFile) { $mapFile = Join-Path (Join-Path $here 'rename-maps') 'land-9a-missing-tables.json' }
$maps = @(Read-LandStaleMap -Path $mapFile)
if ($maps.Count -eq 0) { throw "Мапа $mapFile порожня." }

$periods = @()
if ($null -ne $PeriodKey) { $periods = @(ConvertTo-DocumentIdList $PeriodKey) }
elseif ($Year -gt 0) { $periods = @(1..12 | ForEach-Object { $Year * 100 + $_ }) }
if ($periods.Count -eq 0) { throw 'Задайте -PeriodKey (202606) або -Year (усі 12 місяців).' }

$docs = @()
if ($null -ne $DocumentId) { $docs = @(ConvertTo-DocumentIdList $DocumentId) }
if ($docs.Count -eq 0 -and $ProjectId -le 0) { throw 'Задайте -DocumentId або -ProjectId.' }

Connect-Ecr -BaseUrl $ApiBase -User $User -Password $Password

if ($docs.Count -eq 0) {
    $l = Invoke-Ecr -Method GET -Path "/api/v1/documents?projectId=$ProjectId&limit=100"
    Assert-EcrOk $l 'GET documents'
    $items = if ($l.Json -is [array]) { @($l.Json) } elseif ($null -ne $l.Json.PSObject.Properties['items']) { @($l.Json.items) } else { @($l.Json) }
    $docs = @($items | ForEach-Object { [int]$_.id })
    if ($docs.Count -eq 0) { throw "У проєкті $ProjectId немає документів." }
}

$mode = if ($exec) { 'ВИКОНАННЯ' } else { 'ПЛАН (лише читання)' }
Write-Host "== Перенос значень застарілих таблиць: $mode; документів $($docs.Count), періодів $($periods.Count), записів мапи $($maps.Count) =="

$r = Invoke-LandStaleMove -DocumentId $docs -PeriodKey $periods -Maps $maps -Execute:$exec -OverwriteConflicts:$OverwriteConflicts -ClearSource:$ClearSource -PaceSeconds $PaceSeconds

Write-Host ("Разом: пар таблиць {0}, значень {1}, перенести/перенесено {2}, перезаписано {3}, однакових {4}, конфліктів {5} ({6}), лише для читання {7}, нерозв'язаних колонок (значень) {8}, нерозв'язаних рядків (значень) {9}, таблиць-приймачів немає {10}." -f `
        $r.Pairs, $r.Values, $r.Moved, $r.Overwritten, $r.Same, $r.Conflicts, $(if ($OverwriteConflicts) { 'перезаписано' } else { 'пропущено' }), $r.ReadOnlyTarget, $r.UnresolvedColumnValues, $r.UnresolvedRowValues, $r.UnresolvedTables)
if ($exec) {
    Write-Host ("Записано рядків {0}, відхилено {1}; стерто в джерелі комірок {2}, відхилено рядків {3}." -f $r.RowsWritten, $r.RowsFailed, $r.CellsCleared, $r.ClearFailed)
}
elseif ($r.Blocked.Count -gt 0) {
    Write-Host "Для запису спершу потрібен Reopen аркушів (їх виконує людина/координатор), див. рядки з «!» вище."
}
if ($r.RowsFailed -gt 0 -or $r.ClearFailed -gt 0) { exit 1 }
