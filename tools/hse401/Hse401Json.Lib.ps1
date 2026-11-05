# tools/hse401/Hse401Json.Lib.ps1
# JSON для великих структур витягу (без залежності від PSObject-обгорток).
Set-StrictMode -Version 2.0

function ConvertTo-HseJson {
    <# Мінімальний JSON-серіалізатор (JavaScriptSerializer ламається на PSObject-обгортках). Рядки, числа, bool, null, словники, списки. #>
    param($Value, [System.Text.StringBuilder]$Sb = (New-Object System.Text.StringBuilder))
    function Esc([string]$s) {
        if ($s -cnotmatch '[\\"\x00-\x1f]') { return $s }
        $o = New-Object System.Text.StringBuilder
        foreach ($ch in $s.ToCharArray()) {
            switch ($ch) {
                '"' { [void]$o.Append('\"') } '\' { [void]$o.Append('\\') }
                "`n" { [void]$o.Append('\n') } "`r" { [void]$o.Append('\r') } "`t" { [void]$o.Append('\t') }
                default { if ([int]$ch -lt 32) { [void]$o.AppendFormat('\u{0:x4}', [int]$ch) } else { [void]$o.Append($ch) } }
            }
        }
        return $o.ToString()
    }
    if ($null -eq $Value) { [void]$Sb.Append('null') }
    elseif ($Value -is [string]) { [void]$Sb.Append('"').Append((Esc $Value)).Append('"') }
    elseif ($Value -is [bool]) { [void]$Sb.Append($(if ($Value) { 'true' } else { 'false' })) }
    elseif ($Value -is [int] -or $Value -is [long]) { [void]$Sb.Append([string]$Value) }
    elseif ($Value -is [double] -or $Value -is [decimal]) { [void]$Sb.Append(([double]$Value).ToString('R', [Globalization.CultureInfo]::InvariantCulture)) }
    elseif ($Value -is [System.Collections.IDictionary]) {
        [void]$Sb.Append('{'); $first = $true
        foreach ($k in $Value.Keys) {
            if (-not $first) { [void]$Sb.Append(',') }; $first = $false
            [void]$Sb.Append('"').Append((Esc ([string]$k))).Append('":')
            ConvertTo-HseJson -Value $Value[$k] -Sb $Sb | Out-Null
        }
        [void]$Sb.Append('}')
    }
    elseif ($Value -is [System.Collections.IEnumerable]) {
        [void]$Sb.Append('['); $first = $true
        foreach ($x in $Value) { if (-not $first) { [void]$Sb.Append(',') }; $first = $false; ConvertTo-HseJson -Value $x -Sb $Sb | Out-Null }
        [void]$Sb.Append(']')
    }
    else { [void]$Sb.Append('"').Append((Esc ([string]$Value))).Append('"') }
    return $Sb
}

function Read-HseJson {
    <# Швидке читання великого JSON у словники/масиви (JavaScriptSerializer). #>
    param([Parameter(Mandatory)][string]$Path)
    Add-Type -AssemblyName System.Web.Extensions
    $js = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $js.MaxJsonLength = [int]::MaxValue
    $js.RecursionLimit = 200
    return $js.DeserializeObject([IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8))
}