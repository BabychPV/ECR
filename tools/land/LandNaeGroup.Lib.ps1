# Спільні функції для довідника LAND_NAE_GROUP (LP-10): група потужності (ECW_C11_03, Land_Power на 9a_17) і Category1
# (ECW_C11_11_02) за NAE. Значення беруться з моделі Land (генерує tools/land/model-gen/nae_group_bridges.py зі словників
# C#), прод-даних тут немає. Dot-source: . .\LandNaeGroup.Lib.ps1

function Get-LandNaeGroupRegistryDefinition {
    <# Опис довідника: ті самі поля й первинний ключ, що в моделі Land (nae_group_bridges.py). #>
    return [pscustomobject]@{
        Code   = 'LAND_NAE_GROUP'
        NameEn = 'Land: NAE groups (NAE -> power group of ECW_C11_03, Category1 of ECW_C11_11_02)'
        Fields = @(
            [pscustomobject]@{ Code = 'NAE_NORMALIZED'; DataType = 'String'; IsKey = $true },
            [pscustomobject]@{ Code = 'POWER_GROUP'; DataType = 'Decimal'; IsKey = $false },
            [pscustomobject]@{ Code = 'CATEGORY1'; DataType = 'String'; IsKey = $false })
        Keys   = @([pscustomobject]@{ Code = 'PK'; Fields = @('NAE_NORMALIZED'); IsPrimary = $true; IgnoreCase = $true })
    }
}

function ConvertTo-LandNaeGroupPlan {
    <#
    .SYNOPSIS
        Довідник LAND_NAE_GROUP з моделі Land -> план записів.
    .DESCRIPTION
        Запис: код `N` + 10 hex (як генерує модель), ключ NAE_NORMALIZED (`0788(001)` або `0788 (001)` - обидва
        написання, діалект шаблонів не має заміни тексту), POWER_GROUP (число) і/або CATEGORY1 (4 цифри). Зіпсовані
        записи (код, ключ, число) і повтори ключа без урахування регістру відкидаються й рахуються в Skipped.
    .OUTPUTS
        pscustomobject: Items (Key, EntryCode, Power ([decimal] або $null), Category1 або $null, Display), Skipped.
    #>
    param([Parameter(Mandatory)]$Registry)

    $items = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    $skipped = 0
    foreach ($e in @($Registry.entries)) {
        $key = ([string]$e.fields.NAE_NORMALIZED).Trim()
        $code = ([string]$e.code).Trim()
        $powerText = if ($e.fields.PSObject.Properties['POWER_GROUP']) { ([string]$e.fields.POWER_GROUP).Trim() } else { '' }
        $cat = if ($e.fields.PSObject.Properties['CATEGORY1']) { ([string]$e.fields.CATEGORY1).Trim() } else { '' }
        $power = $null
        $ok = ($key -match '^[0-9]{4}( ?\([0-9]{3}\))?$') -and ($code -match '^N[0-9A-F]{10}$') -and -not $seen.ContainsKey($key.ToUpperInvariant())
        if ($ok -and $powerText.Length -gt 0) {
            $parsed = [decimal]0
            if ([decimal]::TryParse($powerText, [System.Globalization.NumberStyles]::Number, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$parsed) -and $parsed -gt 0) { $power = $parsed }
            else { $ok = $false }
        }
        if ($ok -and $cat.Length -gt 0 -and $cat -notmatch '^[0-9]{4}$') { $ok = $false }
        if ($ok -and $null -eq $power -and $cat.Length -eq 0) { $ok = $false }
        if (-not $ok) { $skipped++; continue }
        $seen[$key.ToUpperInvariant()] = $true
        $items.Add([pscustomobject]@{
                Key = $key; EntryCode = $code; Power = $power; Category1 = $(if ($cat.Length -gt 0) { $cat } else { $null })
                Display = $key })
    }
    return [pscustomobject]@{ Items = @($items | Sort-Object Key); Skipped = $skipped }
}
