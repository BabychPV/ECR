# tools/hse401/Hse401Nae.Lib.ps1
# Чисті функції NAE-вузлів HSE401 (без мережі й без БД): код MPE, джерела NAE з витягу книги, показ "<MPE> — <назва>".
# Підключається з Import-Hse401Nae.ps1, Backfill-Hse401NaeDisplay.ps1 і тестів tools/tests. Split-HseBilingual - з Hse401Stand.Lib.ps1.

function ConvertTo-NaeCode([string]$raw) {
    <# Текст колонки L -> MPE: цифри коротше 4 знаків доповнюються нулями ('81' -> '0081'), решта як є ('3635_POD'). #>
    $t = ([string]$raw -replace '\s+', ' ').Trim()
    if ($t -match '^[0-9]+$' -and $t.Length -lt 4) { return $t.PadLeft(4, '0') }
    return $t
}

function Format-Hse401NaeDisplay {
    <#
    .SYNOPSIS
        Назва вузла SRC для сітки: "<MPE> — <назва>"; коли назва порожня або дорівнює MPE - просто "<MPE>".
    .DESCRIPTION
        MPE - точний текст колонки L Excel (регістр джерела). Ідемпотентно: назва, що вже починається з "<MPE> — ", не префіксується вдруге
        (префікс іншого регістру нормалізується до точного). Порівняння "назва = MPE" не залежить від регістру.
    #>
    param([Parameter(Mandatory)][string]$Mpe, [AllowNull()][AllowEmptyString()][string]$Name)
    $m = $Mpe.Trim()
    $n = if ($null -eq $Name) { '' } else { $Name.Trim() }
    $prefix = $m + ' ' + [char]0x2014 + ' '
    if ($n.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        $rest = $n.Substring($prefix.Length).Trim()
        if ($rest.Length -eq 0 -or $rest.Equals($m, [System.StringComparison]::OrdinalIgnoreCase)) { return $m }
        return $prefix + $rest
    }
    if ($n.Length -eq 0 -or $n.Equals($m, [System.StringComparison]::OrdinalIgnoreCase)) { return $m }
    return $prefix + $n
}

function Get-Hse401NaeSrcCode {
    <# Код вузла SRC: N_<РЕГІОН>_<СУША/МОРЕ>_<MPE> (верхній регістр). #>
    param([string]$Region, [string]$OnOff, [string]$Mpe)
    return ("N_{0}_{1}_{2}" -f $Region.ToUpperInvariant(), $OnOff.ToUpperInvariant(), $Mpe.ToUpperInvariant())
}

function Get-Hse401MpeFromNaeCode {
    <# Запасний хвіст MPE з коду вузла (верхній регістр), коли джерела в витягу немає. $null - код не схожий на SRC. #>
    param([string]$Code)
    if ($Code -match '^N_.+?_(ONSHORE|OFFSHORE)_(.+)$') { return $Matches[2] }
    return $null
}

function Get-Hse401NaeSources {
    <# Витяг книги (hse401-extract.json, rows[].v по літерах колонок) -> джерела NAE: ключ "регіон|суша/море|MPE" -> @{ region; onoff; area; facility; mpe; name; paths; firstRow }. #>
    param([Parameter(Mandatory)]$Data)
    function En([string]$s) { $b = Split-HseBilingual $s; if ($b.En) { return $b.En } else { return $s.Trim() } }
    $sources = [ordered]@{}
    foreach ($row in $Data['rows']) {
        $v = $row['v']
        if (-not ($v['C'] -and $v['E'] -and $v['L'])) { continue }
        $mpe = ConvertTo-NaeCode $v['L']
        $key = "$(En $v['C'])|$(En $v['E'])|$mpe"
        $path = "$(En $(if ($v['F']) { $v['F'] } else { '' }))|$(En $(if ($v['G']) { $v['G'] } else { '' }))"
        if (-not $sources.Contains($key)) {
            $sources[$key] = @{ region = (En $v['C']); onoff = (En $v['E']); area = $(if ($v['F']) { En $v['F'] } else { $null }); facility = $(if ($v['G']) { En $v['G'] } else { $null }); mpe = $mpe; name = $(if ($v['D']) { ($v['D'] -replace '\s+', ' ').Trim() } else { $null }); paths = @{}; firstRow = $row['r'] }
        }
        $s = $sources[$key]; $s.paths[$path] = 1
        if (-not $s.name -and $v['D']) { $s.name = ($v['D'] -replace '\s+', ' ').Trim() }
    }
    return $sources
}
