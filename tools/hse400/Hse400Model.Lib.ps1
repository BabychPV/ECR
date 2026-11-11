# tools/hse400/Hse400Model.Lib.ps1
# Модель шаблону HSE400 (hse400-model.json): читання й перевірка. Мережі не торкається.
#
# Договір із модулем місячних колонок (lane integ-hse400-month-module):
#   - рядок вводу:        RowKey = код компонента AF (Methane, IC4_1, ...), аргумент GCV_HSE400_<таблиця>_<RowKey>;
#   - розрахунковий рядок: RowKey = F_<X> для формули HSE400_<таблиця>_<X>, лише читання;
#   - модуль не читає рядки F_ як ввід і не пише в рядки вводу.
# Префікс F_ потрібен, бо ім'я формули збігається з кодом вводу (Nitrogen у FG_Makat/FG_Export, вісім кодів у *_Internal).

Set-StrictMode -Version 2.0

$script:Hse400ModelHere = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

# таблиця -> (рядків вводу, розрахункових рядків); числа з вивантаження AF Methodologies\HSE400\V1
$script:Hse400Expected = [ordered]@{
    FG_Makat = @(15, 38); FG_HMB = @(34, 58); LPG_HMB = @(34, 58)
    FG_Export = @(16, 41); FG_Internal = @(38, 65); LPG_Internal = @(38, 65)
}
$script:Hse400RowKeyPattern = '^[A-Za-z0-9_.\-]{1,100}\z'
$script:Hse400CodePattern = '^[A-Za-z][A-Za-z0-9_]{0,63}\z'

function Get-Hse400MonthColumns {
    <# 12 місячних колонок M01..M12. #>
    $en = 'January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'
    $ru = 'Январь', 'Февраль', 'Март', 'Апрель', 'Май', 'Июнь', 'Июль', 'Август', 'Сентябрь', 'Октябрь', 'Ноябрь', 'Декабрь'
    for ($i = 1; $i -le 12; $i++) {
        [pscustomobject]@{ Code = ('M{0:00}' -f $i); Month = $i; En = $en[$i - 1]; Ru = $ru[$i - 1] }
    }
}

function Assert-Hse400Model {
    <# Кидає виняток із переліком усіх порушень. #>
    param([Parameter(Mandatory)]$Model)
    $bad = New-Object System.Collections.Generic.List[string]
    foreach ($c in @([string]$Model.template.code, [string]$Model.sheet.code)) {
        if ($c -cnotmatch $script:Hse400CodePattern) { $bad.Add("код '$c' не відповідає EcrCode") }
    }
    if ([string]$Model.template.version -notmatch '^\d+\.\d+\.\d+\.\d+$') { $bad.Add("версія '$($Model.template.version)'") }
    $codes = @($Model.tables | ForEach-Object { [string]$_.code })
    if (($codes -join ',') -cne (@($script:Hse400Expected.Keys) -join ',')) { $bad.Add("таблиці: $($codes -join ',')") }
    foreach ($t in @($Model.tables)) {
        $tc = [string]$t.code
        if ($tc -cnotmatch $script:Hse400CodePattern) { $bad.Add("таблиця '$tc' не відповідає EcrCode") }
        if (-not [string]$t.en -or -not [string]$t.ru) { $bad.Add("${tc}: порожня назва") }
        $seen = @{}
        $calcStarted = $false
        $nIn = 0; $nCalc = 0
        foreach ($r in @($t.rows)) {
            $k = [string]$r.key
            if ($k -cnotmatch $script:Hse400RowKeyPattern) { $bad.Add("$tc/${k}: RowKey") }
            if ($seen.ContainsKey($k.ToLowerInvariant())) { $bad.Add("$tc/${k}: повтор ключа") }
            $seen[$k.ToLowerInvariant()] = $true
            if (-not [string]$r.en -or -not [string]$r.ru) { $bad.Add("$tc/${k}: порожній підпис") }
            $isF = $k.StartsWith('F_', [StringComparison]::Ordinal)
            switch ([string]$r.kind) {
                'Input' { $nIn++; if ($isF) { $bad.Add("$tc/${k}: ввід із префіксом F_") }; if ($calcStarted) { $bad.Add("$tc/${k}: ввід після розрахункових") } }
                'Calc' { $nCalc++; $calcStarted = $true; if (-not $isF) { $bad.Add("$tc/${k}: розрахунковий без префікса F_") } }
                default { $bad.Add("$tc/${k}: kind '$($r.kind)'") }
            }
        }
        if ($script:Hse400Expected.Contains($tc)) {
            $e = $script:Hse400Expected[$tc]
            if ($nIn -ne $e[0] -or $nCalc -ne $e[1]) { $bad.Add("${tc}: ввід/розрахунок $nIn/$nCalc, очікується $($e[0])/$($e[1])") }
        }
    }
    if ($bad.Count) { throw ("hse400-model.json: " + ($bad -join '; ')) }
}

function Read-Hse400Model {
    param([string]$Path = (Join-Path $script:Hse400ModelHere 'hse400-model.json'))
    $m = [IO.File]::ReadAllText($Path, [Text.Encoding]::UTF8) | ConvertFrom-Json
    Assert-Hse400Model $m
    return $m
}
