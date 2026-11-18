<#
.SYNOPSIS
    B175: дефолти комірок рядка моделі Land (`rows[].cellDefaults` = { "<код колонки>": "<текст>" }) для обох шляхів
    запису рядків білдера — SQL-масового (cfg.RowDef.CellDefaultsJson) і API (PUT .../rows/{key}, поле cellDefaults).

.DESCRIPTION
    Чисті функції без звернень до API/SQL (тестується Pester: tools/tests/Land-RowCellDefaults.Tests.ps1).
    * Lookup — КОД запису довідника (як defaultValue колонки); "" — прибрати дефолт колонки в цьому рядку.
    * Порядок ключів — ordinal (як RowDef.SetCellDefaults), тож той самий набір дає той самий JSON.
    * Рядок без cellDefaults (або з порожнім об'єктом) -> $null: білдер не чіпає ні колонку SQL, ні поле API
      (сервер трактує відсутнє поле як «не змінювати»).
#>

function Get-LandRowCellDefaults {
    [CmdletBinding()]
    param($Row)
    if ($null -eq $Row) { return $null }
    $src = $Row.cellDefaults
    if ($null -eq $src) { return $null }
    $names = New-Object System.Collections.Generic.List[string]
    if ($src -is [System.Collections.IDictionary]) {
        foreach ($k in $src.Keys) { $names.Add([string]$k) }
    } else {
        foreach ($p in $src.PSObject.Properties) { $names.Add([string]$p.Name) }
    }
    if ($names.Count -eq 0) { return $null }
    $names.Sort([System.StringComparer]::Ordinal)
    $lrcdResult = [ordered]@{}
    foreach ($n in $names) {
        $v = if ($src -is [System.Collections.IDictionary]) { $src[$n] } else { $src.$n }
        $lrcdResult[$n] = if ($null -eq $v) { '' } else { [string]$v }
    }
    return $lrcdResult
}

function ConvertTo-LandRowCellDefaultsJson {
    [CmdletBinding()]
    param($Row)
    $lrcdMap = Get-LandRowCellDefaults $Row
    if ($null -eq $lrcdMap) { return $null }
    return (ConvertTo-Json -InputObject $lrcdMap -Compress)
}
