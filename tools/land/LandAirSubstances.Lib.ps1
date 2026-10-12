# tools/land/LandAirSubstances.Lib.ps1
# Чисті функції для Import-LandAirSubstances.ps1 (без мережі й без БД) — щоб їх можна було перевірити тестом
# на синтетичних рядках. Значень прод-даних тут немає і бути не може.

function ConvertTo-LandAirSubstancePlan {
    <#
    .SYNOPSIS
        Рядки AF_PermitConstant (код, назви EN/RU, формула, фізстан) -> план записів довідника LAND_AIR_SUBSTANCE.
    .DESCRIPTION
        Код речовини — 3-4 цифри (формат, який читає форма B рушія: {ключ}_{код}); провідні нулі знімаються
        (0301 -> 301), решта (текстові, порожні, довші) пропускається з обліком. Код запису довідника — `E<код>`:
        EcrCode вимагає літери першим символом, тому число лежить у полі SUBSTANCE_CODE (як у LAND_WASTE_SUBSTANCE).
        Дубль коду в джерелі — береться перший, решта рахується.
    .OUTPUTS
        pscustomobject: Items (Code, EntryCode, DisplayEn, DisplayRu, Formula, StateEn, StateRu), Skipped, Duplicates, Normalized.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Rows)

    $items = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    $skipped = 0
    $duplicates = 0
    $normalized = 0

    foreach ($r in $Rows) {
        $raw = if ($null -eq $r.Code) { '' } else { ([string]$r.Code).Trim() }
        if ($raw -notmatch '^[0-9]{3,5}$') { $skipped++; continue }
        $code = $raw.TrimStart('0')
        if ($code -ne $raw) { $normalized++ }
        if ($code -notmatch '^[1-9][0-9]{2,3}$') { $skipped++; continue }
        if ($seen.ContainsKey($code)) { $duplicates++; continue }
        $seen[$code] = $true

        $en = if ([string]::IsNullOrWhiteSpace([string]$r.NameEn)) { '' } else { ([string]$r.NameEn).Trim() }
        $ru = if ([string]::IsNullOrWhiteSpace([string]$r.NameRu)) { '' } else { ([string]$r.NameRu).Trim() }
        if ($en.Length -eq 0) { $en = if ($ru.Length -gt 0) { $ru } else { $code } }
        if ($en.Length -gt 200) { $en = $en.Substring(0, 200) }
        if ($ru.Length -gt 200) { $ru = $ru.Substring(0, 200) }

        $items.Add([pscustomobject]@{
                Code      = $code
                EntryCode = "E$code"
                DisplayEn = $en
                DisplayRu = $ru
                Formula   = if ($null -eq $r.Formula) { '' } else { ([string]$r.Formula).Trim() }
                StateEn   = if ($null -eq $r.StateEn) { '' } else { ([string]$r.StateEn).Trim() }
                StateRu   = if ($null -eq $r.StateRu) { '' } else { ([string]$r.StateRu).Trim() }
            })
    }

    return [pscustomobject]@{
        Items      = @($items | Sort-Object { [int]$_.Code })
        Skipped    = $skipped
        Duplicates = $duplicates
        Normalized = $normalized
    }
}

function Assert-LocalSourceDatabase {
    <#
    .SYNOPSIS
        Джерело (локальна копія прода) читається лише з цієї машини, і лише з явно підтвердженою базою.
    #>
    param(
        [Parameter(Mandatory)][string]$Server,
        [Parameter(Mandatory)][string]$Database,
        [string]$ConfirmSource
    )
    if ($Server -notmatch '^[A-Za-z0-9_\-\.\\,:\(\)\[\]]{1,128}$') {
        throw "Ім'я SQL-сервера джерела '$Server' містить недопустимі символи. Відмова."
    }
    $s = $Server.Trim()
    $s = $s -replace '^(tcp|np|lpc):', ''
    $s = ($s -split ',')[0]
    $s = ($s -split '\\')[0]
    $s = $s.Trim().Trim('[', ']')
    $local = ($s -eq '.' -or $s -eq '(local)' -or (Test-LocalHostName $s) -or
        (-not [string]::IsNullOrEmpty($env:COMPUTERNAME) -and $s -ieq $env:COMPUTERNAME))
    if (-not $local) {
        throw "SQL-сервер джерела '$Server' не локальний: копія прода читається лише з цієї машини. Відмова."
    }
    if ([string]::IsNullOrWhiteSpace($Database) -or $Database -notmatch '^[A-Za-z0-9_\-\.]{1,128}$') {
        throw "Ім'я бази джерела '$Database' недопустиме. Відмова."
    }
    if ([string]::IsNullOrWhiteSpace($ConfirmSource) -or $ConfirmSource.Trim() -ine $Database) {
        throw "Читання джерела потребує явного -ConfirmSource $Database (ім'я бази копії, з якої читаємо). Без нього скрипт нічого не читає."
    }
}
