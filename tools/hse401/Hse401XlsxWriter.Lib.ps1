# tools/hse401/Hse401XlsxWriter.Lib.ps1
# Мінімальний записувач .xlsx (один аркуш, текстові комірки inlineStr) для вхідного файлу імпорту дерева NAE.
# Без пакетів: System.IO.Compression. Windows PowerShell 5.1 і PowerShell 7.

Set-StrictMode -Version 2.0

function ConvertTo-XmlText {
    param([string]$Text)
    $t = [System.Security.SecurityElement]::Escape($Text)
    return $t
}

function New-Hse401Xlsx {
    <# Rows: список масивів рядків (перший - заголовок); $null/'' -> порожня комірка. #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$SheetName, [Parameter(Mandatory)][System.Collections.IEnumerable]$Rows)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $sheet = New-Object System.Text.StringBuilder
    [void]$sheet.Append('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>')
    $r = 0
    foreach ($row in $Rows) {
        $r++
        [void]$sheet.Append("<row r=`"$r`">")
        $c = 0
        foreach ($cell in $row) {
            $c++
            if ($null -eq $cell -or ([string]$cell).Length -eq 0) { continue }
            $ref = [Hse401.XlsxReader]::ColLetters($c) + $r
            $txt = ConvertTo-XmlText ([string]$cell)
            [void]$sheet.Append("<c r=`"$ref`" t=`"inlineStr`"><is><t xml:space=`"preserve`">$txt</t></is></c>")
        }
        [void]$sheet.Append('</row>')
    }
    [void]$sheet.Append('</sheetData></worksheet>')

    $files = [ordered]@{
        '[Content_Types].xml'        = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>'
        '_rels/.rels'                = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'
        'xl/workbook.xml'            = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="' + (ConvertTo-XmlText $SheetName) + '" sheetId="1" r:id="rId1"/></sheets></workbook>'
        'xl/_rels/workbook.xml.rels' = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>'
        'xl/worksheets/sheet1.xml'   = $sheet.ToString()
    }
    $fs = [System.IO.File]::Create($Path)
    try {
        $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($name in $files.Keys) {
                $entry = $zip.CreateEntry($name)
                $w = New-Object System.IO.StreamWriter($entry.Open(), (New-Object System.Text.UTF8Encoding($false)))
                try { $w.Write($files[$name]) } finally { $w.Dispose() }
            }
        }
        finally { $zip.Dispose() }
    }
    finally { $fs.Dispose() }
}
