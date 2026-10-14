<#
.SYNOPSIS
    Формат ключа рядка документа Land (rowKey) і його розбір. Лише функції, без побічних ефектів.
.DESCRIPTION
    Два формати:
      Legacy  p<doc>-<seq:0000>             (p10-0001)  - позиція рядка в таблиці; ключ повторюється в КОЖНІЙ таблиці документа;
      Table   p<doc>-<TableCode>-<seq:0000> (p10-T3_00-0001) - з кодом таблиці: унікальний у межах документа і періоду.
    Чому: calc.CalculationResult має DocumentId + SourceRowKey, але не таблицю. За Legacy результат рядка 1 однієї
    таблиці неможливо відрізнити від рядка 1 іншої (діагноз Б, LAND-5094): harness приписував його всім таблицям-кандидатам.
    Код таблиці - ідентифікатор без дефісів ([A-Za-z][A-Za-z0-9_]*), тому розбір однозначний.

    Місце підключення завантажувача: Load-LandInputCells.ps1 -RowKeyFormat Table -> New-LandInputCellPlan -RowKeyFormat.
    Завантажувач повного набору (Load-Full.ps1) лежить ПОЗА репозиторієм і тут не змінюється: для нього - ця
    функція New-LandRowKey (той самий ключ, що очікує harness).

    Правило імен (LandScriptVariableCollisionTests): локальні змінні не мають збігатися з іменами параметрів функцій файлу.
#>

function New-LandRowKey {
    <#
    .SYNOPSIS  p<doc>-<TableCode>-<seq:0000> (Table, за замовчуванням) або p<doc>-<seq:0000> (Legacy).
    #>
    param(
        [Parameter(Mandatory)][int]$DocId,
        [string]$TableCode = '',
        [Parameter(Mandatory)][int]$Index,
        [ValidateSet('Table', 'Legacy')][string]$KeyFormat = 'Table'
    )
    if ($DocId -lt 0) { throw "DocId must be >= 0." }
    if ($Index -lt 1) { throw "Index must be >= 1." }
    if ($KeyFormat -eq 'Legacy') { return ('p{0}-{1:0000}' -f $DocId, $Index) }
    if ($TableCode -notmatch '^[A-Za-z][A-Za-z0-9_]*$') { throw "TableCode '$TableCode' is not a valid table code (letters, digits, underscore; no hyphen)." }
    return ('p{0}-{1}-{2:0000}' -f $DocId, $TableCode, $Index)
}

function ConvertFrom-LandRowKey {
    <#
    .SYNOPSIS  Розбір ключа: { Format = Table|Legacy|Unknown; Doc; Table; Index }. Unknown - ключ не нашого вигляду.
    #>
    param([AllowEmptyString()][string]$KeyText)
    $m = [regex]::Match($KeyText, '^p(?<doc>\d+)-(?<tbl>[A-Za-z][A-Za-z0-9_]*)-(?<seq>\d+)$')
    if ($m.Success) {
        return [pscustomobject]@{ Format = 'Table'; Doc = [int]$m.Groups['doc'].Value; Table = $m.Groups['tbl'].Value; Index = [int]$m.Groups['seq'].Value }
    }
    $m = [regex]::Match($KeyText, '^p(?<doc>\d+)-(?<seq>\d+)$')
    if ($m.Success) {
        return [pscustomobject]@{ Format = 'Legacy'; Doc = [int]$m.Groups['doc'].Value; Table = $null; Index = [int]$m.Groups['seq'].Value }
    }
    return [pscustomobject]@{ Format = 'Unknown'; Doc = $null; Table = $null; Index = $null }
}
