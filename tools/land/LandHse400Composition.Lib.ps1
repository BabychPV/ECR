# tools/land/LandHse400Composition.Lib.ps1
# Чисті функції для Import-LandHse400Composition.ps1 (без мережі й без БД) - щоб їх можна було перевірити тестом
# на синтетичних рядках. Значень прод-даних тут немає і бути не може.
#
# Перенос складу газу HSE400 (dbo.CalcResult_AirGasCompositions_HSE400, 494 колонки, рядок = місяць) у нормалізований
# довідник LAND_HSE400_COMPOSITION: (YEAR, MONTH, STREAM, INDICATOR) -> VALUE. Потік = префікс колонки (словник
# PrefixProvider старої системи), показник = суфікс колонки в канонічному написанні.

$script:LandHse400Prefixes = @('FG_Makat', 'FG_HMB', 'FG_Export', 'FG_Internal', 'LPG_HMB', 'LPG_Internal')
$script:LandHse400BasePrefixes = [ordered]@{
    'Makat'               = 'FG_Makat'
    'Fuel gas (HMB)'      = 'FG_HMB'
    'Fuel gas (Export)'   = 'FG_Export'
    'Fuel gas (Internal)' = 'FG_Internal'
    'LPG (HMB)'           = 'LPG_HMB'
    'LPG (Internal)'      = 'LPG_Internal'
}

function Get-LandHse400Prefixes { return @($script:LandHse400Prefixes) }

function Get-LandHse400StreamPrefix {
    <#
    .SYNOPSIS
        Правило старої системи (PrefixProvider + fuelTypeArgument): перший ключ словника, що міститься в тексті
        палива/потоку або містить його, без урахування регістру. Порожній текст - немає потоку.
    #>
    param([AllowNull()][AllowEmptyString()][string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }
    $t = $Text.Trim().ToLowerInvariant()
    foreach ($key in $script:LandHse400BasePrefixes.Keys) {
        $k = $key.ToLowerInvariant()
        if ($t.Contains($k) -or $k.Contains($t)) { return [string]$script:LandHse400BasePrefixes[$key] }
    }
    return $null
}

function Get-LandHse400StreamEntryCode {
    <# Код запису словника потоків: S + 10 hex SHA1 нижнього регістру тексту (UTF-8) - збігається з моделлю Land. #>
    param([Parameter(Mandatory)][string]$Text)
    $sha = [System.Security.Cryptography.SHA1]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Text.ToLowerInvariant()))
    }
    finally { $sha.Dispose() }
    return 'S' + (([System.BitConverter]::ToString($bytes) -replace '-', '').Substring(0, 10).ToUpperInvariant())
}

function ConvertTo-LandHse400StreamPlan {
    <#
    .SYNOPSIS
        Тексти палива/потоку з Land-полів -> записи словника LAND_HSE400_STREAM (текст -> префікс) за правилом старої
        системи. Незбіжні тексти лише рахуються (для них склад не береться - так і в старій системі).
    .OUTPUTS
        pscustomobject: Items (Text, Prefix, EntryCode), Unmatched, Duplicates.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][AllowEmptyString()][AllowNull()][string[]]$Texts)

    $items = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    $unmatched = 0
    $duplicates = 0
    foreach ($raw in $Texts) {
        if ([string]::IsNullOrWhiteSpace($raw)) { continue }
        $trimmed = $raw.Trim()
        $prefix = Get-LandHse400StreamPrefix -Text $trimmed
        if ($null -eq $prefix) { $unmatched++; continue }
        $key = $trimmed.ToLowerInvariant()
        if ($seen.ContainsKey($key)) { $duplicates++; continue }
        $seen[$key] = $true
        $display = if ($trimmed.Length -gt 200) { $trimmed.Substring(0, 200) } else { $trimmed }
        $items.Add([pscustomobject]@{ Text = $trimmed; Display = $display; Prefix = $prefix; EntryCode = Get-LandHse400StreamEntryCode -Text $trimmed })
    }
    return [pscustomobject]@{ Items = @($items | Sort-Object Prefix, Text); Unmatched = $unmatched; Duplicates = $duplicates }
}

function ConvertTo-LandHse400Indicator {
    <# Канонічне ім'я показника: `_20` -> `20`, `_0` -> `0` (правило альтернативних імен старої системи). #>
    param([Parameter(Mandatory)][string]$Suffix)
    $s = $Suffix.Trim()
    $s = [regex]::Replace($s, '_20(?=$|_)', '20')
    $s = [regex]::Replace($s, '_0(?=$|_)', '0')
    return $s
}

function ConvertTo-LandHse400CompositionPlan {
    <#
    .SYNOPSIS
        Рядки таблиці (Year, Month, Columns: колонка -> значення) -> записи довідника LAND_HSE400_COMPOSITION.
    .DESCRIPTION
        - потік = префікс колонки з шести відомих; колонки без префікса (EFID, ID, DateTime ...) пропускаються;
        - показник = канонічний суфікс (ConvertTo-LandHse400Indicator); значення null/нечислове пропускається;
        - HMB: EMercaptan_mass береться з колонки EMercaptanV2_mass (як F_Air_GetPropertyValueFromHSE400);
        - запасна: C6_C10_mass порожній -> береться C6_1_mass (ECW_C11_01/02, ECW_C12_01);
        - дубль (рік, місяць, потік, показник) - береться перший, решта рахується.
        Значення віддаються рядком у інваріантній культурі; нічого не друкується.
    .OUTPUTS
        pscustomobject: Items (Year, Month, Stream, Indicator, Value, EntryCode), Skipped, Duplicates, Fallbacks, Overrides.
    #>
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Rows)

    $inv = [System.Globalization.CultureInfo]::InvariantCulture
    $items = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    $skipped = 0
    $duplicates = 0
    $fallbacks = 0
    $overrides = 0

    foreach ($row in $Rows) {
        $year = [int]$row.Year
        $month = [int]$row.Month
        if ($month -lt 1 -or $month -gt 12) { $skipped++; continue }

        # потік -> (канонічний показник -> значення)
        $byStream = @{}
        foreach ($name in @($row.Columns.Keys)) {
            $prefix = $script:LandHse400Prefixes | Where-Object { ([string]$name).StartsWith($_ + '_', [StringComparison]::Ordinal) } | Select-Object -First 1
            if ($null -eq $prefix) { continue }
            $value = $row.Columns[$name]
            if ($null -eq $value -or $value -is [System.DBNull]) { $skipped++; continue }
            $number = [decimal]0
            if (-not [decimal]::TryParse([string]::Format($inv, '{0}', $value), [System.Globalization.NumberStyles]::Float, $inv, [ref]$number)) { $skipped++; continue }
            $indicator = ConvertTo-LandHse400Indicator -Suffix ([string]$name).Substring($prefix.Length + 1)
            if (-not $byStream.ContainsKey($prefix)) { $byStream[$prefix] = @{} }
            if ($byStream[$prefix].ContainsKey($indicator)) { $duplicates++; continue }
            $byStream[$prefix][$indicator] = $number
        }

        foreach ($prefix in @($byStream.Keys)) {
            $ind = $byStream[$prefix]

            # HMB: ЕМеркаптан береться з V2 (лише коли звичайна колонка існує - як у старій системі).
            if ($prefix -like '*HMB*' -and $row.Columns.ContainsKey($prefix + '_EMercaptan_mass')) {
                if ($ind.ContainsKey('EMercaptanV2_mass')) { $ind['EMercaptan_mass'] = $ind['EMercaptanV2_mass']; $overrides++ }
                else { $null = $ind.Remove('EMercaptan_mass') }
            }

            # Запасна колонка гексану.
            if (-not $ind.ContainsKey('C6_C10_mass') -and $ind.ContainsKey('C6_1_mass')) {
                $ind['C6_C10_mass'] = $ind['C6_1_mass']
                $fallbacks++
            }

            foreach ($indicator in $ind.Keys) {
                $key = '{0}|{1}|{2}|{3}' -f $year, $month, $prefix, $indicator
                if ($seen.ContainsKey($key)) { $duplicates++; continue }
                $seen[$key] = $true
                $safe = [regex]::Replace($indicator, '[^A-Za-z0-9_]', '_')
                $code = 'H{0:D4}{1:D2}_{2}_{3}' -f $year, $month, $prefix, $safe
                if ($code.Length -gt 64) { $code = $code.Substring(0, 64) }
                $items.Add([pscustomobject]@{
                        Year      = $year
                        Month     = $month
                        Stream    = $prefix
                        Indicator = $indicator
                        Value     = [string]::Format($inv, '{0}', $ind[$indicator])
                        EntryCode = $code
                    })
            }
        }
    }

    return [pscustomobject]@{
        Items      = @($items | Sort-Object Year, Month, Stream, Indicator)
        Skipped    = $skipped
        Duplicates = $duplicates
        Fallbacks  = $fallbacks
        Overrides  = $overrides
    }
}

function ConvertTo-LandHse400SubstancePlan {
    <#
    .SYNOPSIS
        Довідник LAND_HSE400_SUBSTANCE (код речовини -> показник складу `{хімічна назва}_mass`) -> план записів.
    .DESCRIPTION
        Джерело - модель Land (land-template.model.json, registries[LAND_HSE400_SUBSTANCE]); її генерує
        tools/land/model-gen/hse400_bridges.py зі словника старого бекенду PollutionCodeService.GetMassName. Значень
        прод-даних тут немає: це довідкова відповідність «код -> ім'я колонки». Код запису - `C<код>` (EcrCode вимагає
        літери першим символом; число лежить у ключовому полі SUBSTANCE_CODE).
    .PARAMETER Registry
        Об'єкт довідника з моделі (code, entries[].fields.SUBSTANCE_CODE/INDICATOR).
    .OUTPUTS
        pscustomobject: Items (Code, EntryCode, Indicator, Display), Skipped.
    #>
    param([Parameter(Mandatory)]$Registry)

    $items = New-Object System.Collections.Generic.List[object]
    $seen = @{}
    $skipped = 0
    foreach ($e in @($Registry.entries)) {
        $code = ([string]$e.fields.SUBSTANCE_CODE).Trim()
        $indicator = ([string]$e.fields.INDICATOR).Trim()
        if ($code -notmatch '^[1-9][0-9]{2,3}$' -or $indicator -notmatch '^[A-Za-z][A-Za-z0-9_]*_mass$' -or $seen.ContainsKey($code)) { $skipped++; continue }
        $seen[$code] = $true
        $items.Add([pscustomobject]@{ Code = $code; EntryCode = "C$code"; Indicator = $indicator; Display = "$code -> $indicator" })
    }
    return [pscustomobject]@{ Items = @($items | Sort-Object { [int]$_.Code }); Skipped = $skipped }
}

function Convert-LandHse400CompositionMassFormula {
    <#
    .SYNOPSIS
        Підставляє фрагмент складу по речовині замість токена `@CompositionMass` у формулі версії методології.
    .DESCRIPTION
        Токен - рівно `@CompositionMass` (не префікс довшого імені). Список аргументів (`;`) втрачає
        `@CompositionMass` і отримує аргументи потоку з фрагмента, якщо їх ще немає. Повторний виклик на вже
        замінену формулу - без змін (Changed = $false).
    .OUTPUTS
        pscustomobject: Changed, Expression, ArgumentsCsv.
    #>
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Expression,
        # Без типу [string]: $null мусить лишитися $null (порожній рядок «оголошує нуль аргументів», ECR-CALC-0432).
        [AllowNull()][AllowEmptyString()]$ArgumentsCsv = $null,
        [Parameter(Mandatory)][string]$Fragment,
        [string[]]$StreamArguments = @()
    )

    $pattern = '@CompositionMass(?![A-Za-z0-9_])'
    if ($Expression -notmatch $pattern) {
        return [pscustomobject]@{ Changed = $false; Expression = $Expression; ArgumentsCsv = $ArgumentsCsv }
    }
    $newExpression = [regex]::Replace($Expression, $pattern, { param($m) $Fragment })

    $newArguments = $ArgumentsCsv
    if ($null -ne $ArgumentsCsv) {
        $tokens = New-Object System.Collections.Generic.List[string]
        foreach ($t in ($ArgumentsCsv -split ';')) {
            $x = $t.Trim()
            if ($x.Length -eq 0 -or $x -ieq '@CompositionMass') { continue }
            $tokens.Add($x)
        }
        foreach ($a in $StreamArguments) {
            $token = '@' + $a.TrimStart('@')
            if (-not ($tokens | Where-Object { $_ -ieq $token })) { $tokens.Add($token) }
        }
        $newArguments = (($tokens -join ';') + ';')
    }
    return [pscustomobject]@{ Changed = $true; Expression = $newExpression; ArgumentsCsv = $newArguments }
}

function Get-LandHse400RegistryDefinitions {
    <# Опис довідників: ті самі поля й складений ключ, що в моделі Land (hse400_bridges.py). #>
    return [pscustomobject]@{
        Substance   = [pscustomobject]@{
            Code   = 'LAND_HSE400_SUBSTANCE'
            NameEn = 'Land: HSE400 composition indicator by substance code (SUBSTANCE_CODE -> {indicator}_mass)'
            Fields = @(
                [pscustomobject]@{ Code = 'SUBSTANCE_CODE'; DataType = 'String'; IsKey = $true },
                [pscustomobject]@{ Code = 'INDICATOR'; DataType = 'String'; IsKey = $false })
            Keys   = @([pscustomobject]@{ Code = 'PK'; Fields = @('SUBSTANCE_CODE'); IsPrimary = $true; IgnoreCase = $true })
        }
        Stream      = [pscustomobject]@{
            Code   = 'LAND_HSE400_STREAM'
            NameEn = 'Land: HSE400 stream dictionary (fuel/stream text -> column prefix)'
            Fields = @(
                [pscustomobject]@{ Code = 'TEXT'; DataType = 'String'; IsKey = $true },
                [pscustomobject]@{ Code = 'PREFIX'; DataType = 'String'; IsKey = $false })
            Keys   = @([pscustomobject]@{ Code = 'PK'; Fields = @('TEXT'); IsPrimary = $true; IgnoreCase = $true })
        }
        Composition = [pscustomobject]@{
            Code   = 'LAND_HSE400_COMPOSITION'
            NameEn = 'Land: HSE400 gas composition (period x stream x indicator), normalized'
            Fields = @(
                [pscustomobject]@{ Code = 'YEAR'; DataType = 'Int'; IsKey = $true },
                [pscustomobject]@{ Code = 'MONTH'; DataType = 'Int'; IsKey = $true },
                [pscustomobject]@{ Code = 'STREAM'; DataType = 'String'; IsKey = $true },
                [pscustomobject]@{ Code = 'INDICATOR'; DataType = 'String'; IsKey = $true },
                [pscustomobject]@{ Code = 'VALUE'; DataType = 'Decimal'; IsKey = $false })
            Keys   = @([pscustomobject]@{ Code = 'PK'; Fields = @('YEAR', 'MONTH', 'STREAM', 'INDICATOR'); IsPrimary = $true; IgnoreCase = $true })
        }
    }
}
