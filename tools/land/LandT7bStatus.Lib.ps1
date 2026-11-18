# tools/land/LandT7bStatus.Lib.ps1
# B26 (P-0118-25, D-425): статус рядків T7b_00 для фільтра активності ECW_C09_02_01. Модель (gen_ecr_model.py) дає таблиці
# колонку Land_Status і statusPresets { column; registry; entry; rows; emptyRows }: рядки речовин - Running, рядок об'єму
# S3_9 - порожньо (його не рахує жодна методологія з фільтром). Нові колонки в НАЯВНИХ документах порожні, тож без запису
# статусу фільтр виключив би й рядки речовин. Чиста логіка без мережі; запис робить Build-LandTemplate.ps1 -Stage T7bStatus.
# Сумісна з Windows PowerShell 5.1 і PowerShell 7.

function Test-LandStatusCellFilled {
    <# Комірка статусу має значення: число/рядок id запису, або об'єкт з id/entryId/value. Порожньо - $null, '' чи isEmpty. #>
    param($Value)
    if ($null -eq $Value) { return $false }
    if ($Value -is [string]) { return $Value.Trim().Length -gt 0 }
    if ($Value -is [ValueType]) { return $true }
    foreach ($p in 'isEmpty') { if ($Value.PSObject.Properties[$p] -and [bool]$Value.$p) { return $false } }
    foreach ($p in 'id', 'entryId', 'registryEntryId', 'value') {
        if ($Value.PSObject.Properties[$p] -and $null -ne $Value.$p -and ([string]$Value.$p).Length -gt 0) { return $true }
    }
    return $false
}

function Get-LandStatusPresetPlan {
    <#
    .SYNOPSIS
        Що записати в одну таблицю одного документа/періоду: статус Running у рядки statusPresets.rows, де комірки статусу
        ще немає. Значення, яке вже стоїть (зокрема поставлене людиною), не перезаписується; рядки emptyRows не пишуться
        ніколи (лише рахуються, і окремо - якщо в них хтось поставив статус).
    .PARAMETER Presets   statusPresets таблиці з моделі.
    .PARAMETER Rows      рядки зрізу GET /documents/{id}/tables/{ti}: rowKey, rowVersion, cells { код -> значення }.
    .PARAMETER EntryId   id запису довідника (statusPresets.entry) на сервері.
    .OUTPUTS
        Rows (для PATCH: rowKey, baseVersion, cells) і лічильники Write, Kept, Missing, EmptyRows, EmptyWithValue.
    #>
    param([Parameter(Mandatory)]$Presets, [object[]]$Rows = @(), [Parameter(Mandatory)][string]$EntryId)
    $column = [string]$Presets.column
    $byKey = @{}
    foreach ($r in @($Rows)) { if ($r) { $byKey[[string]$r.rowKey] = $r } }
    $patch = New-Object 'System.Collections.Generic.List[object]'
    $res = [ordered]@{ Rows = $null; Write = 0; Kept = 0; Missing = 0; EmptyRows = 0; EmptyWithValue = 0 }
    foreach ($key in @($Presets.rows)) {
        $rk = [string]$key
        if (-not $byKey.ContainsKey($rk)) { $res.Missing++; continue }
        $row = $byKey[$rk]
        $cur = $null
        if ($row.cells -and $row.cells.PSObject.Properties[$column]) { $cur = $row.cells.$column }
        if (Test-LandStatusCellFilled $cur) { $res.Kept++; continue }
        $patch.Add([pscustomobject][ordered]@{ rowKey = $rk; baseVersion = [string]$row.rowVersion
                cells = @(@{ columnCode = $column; isEmpty = $false; value = $EntryId }) })
        $res.Write++
    }
    foreach ($key in @($Presets.emptyRows)) {
        $rk = [string]$key
        if (-not $byKey.ContainsKey($rk)) { continue }
        $res.EmptyRows++
        $row = $byKey[$rk]
        if ($row.cells -and $row.cells.PSObject.Properties[$column] -and (Test-LandStatusCellFilled $row.cells.$column)) { $res.EmptyWithValue++ }
    }
    $res.Rows = $patch.ToArray()
    return [pscustomobject]$res
}
