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
    # методологія (модуль місячних колонок): код = простір імен аргументів і формул, версія <= 20 символів
    if ($null -eq $Model.PSObject.Properties['methodology'] -or $null -eq $Model.methodology) { $bad.Add('немає розділу methodology') }
    else {
        $mm = $Model.methodology
        $mc = if ($mm.PSObject.Properties['code']) { [string]$mm.code } else { '' }
        $mv = if ($mm.PSObject.Properties['version']) { [string]$mm.version } else { '' }
        if ($mc -cnotmatch $script:Hse400CodePattern) { $bad.Add("методологія: код '$mc' не відповідає EcrCode") }
        if ($mv.Length -lt 1 -or $mv.Length -gt 20) { $bad.Add("методологія: версія '$mv'") }
        foreach ($n in 'en', 'ru') { if (-not $mm.PSObject.Properties[$n] -or -not [string]$mm.$n) { $bad.Add("методологія: порожня назва $n") } }
        if ($mm.PSObject.Properties['formulas'] -and $null -ne $mm.formulas) {
            foreach ($f in @($mm.formulas)) {
                $fc = if ($f.PSObject.Properties['code']) { [string]$f.code } else { '' }
                if ($fc -cnotmatch $script:Hse400CodePattern) { $bad.Add("формула '$fc' не відповідає EcrCode") }
                if (-not $f.PSObject.Properties['expression'] -or -not [string]$f.expression) { $bad.Add("формула ${fc}: порожній вираз") }
            }
        }
    }
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
    # золоті тести методології: адреса розрахункового рядка + ввід у рядки вводу тієї ж таблиці (один місяць)
    if ($null -ne $Model.PSObject.Properties['methodology'] -and $null -ne $Model.methodology -and
        $Model.methodology.PSObject.Properties['tests'] -and $null -ne $Model.methodology.tests) {
        $kinds = @{}
        foreach ($t in @($Model.tables)) { foreach ($r in @($t.rows)) { $kinds[("{0}/{1}" -f $t.code, $r.key)] = [string]$r.kind } }
        $seenTests = @{}
        $isNumber = { param($v) $null -ne $v -and ($v -is [double] -or $v -is [decimal] -or $v -is [int] -or $v -is [long]) }
        foreach ($x in @($Model.methodology.tests)) {
            $get = { param($n) if ($x.PSObject.Properties[$n]) { $x.$n } else { $null } }
            $xc = [string](& $get 'code')
            if ($xc -cnotmatch $script:Hse400CodePattern) { $bad.Add("тест '$xc' не відповідає EcrCode"); continue }
            if ($seenTests.ContainsKey($xc.ToLowerInvariant())) { $bad.Add("тест ${xc}: повтор коду") }
            $seenTests[$xc.ToLowerInvariant()] = $true
            $xt = [string](& $get 'table'); $xr = [string](& $get 'row'); $xm = [string](& $get 'month')
            if ($kinds["$xt/$xr"] -ne 'Calc') { $bad.Add("тест ${xc}: $xt/$xr не розрахунковий рядок моделі") }
            if ($xm -cnotmatch '^M(0[1-9]|1[0-2])\z') { $bad.Add("тест ${xc}: місяць '$xm'") }
            if (-not (& $isNumber (& $get 'expected'))) { $bad.Add("тест ${xc}: expected не число") }
            $tol = & $get 'tolerance'
            if (-not (& $isNumber $tol) -or [double]$tol -lt 0) { $bad.Add("тест ${xc}: tolerance") }
            $inp = & $get 'inputs'
            if ($null -eq $inp -or @($inp.PSObject.Properties).Count -eq 0) { $bad.Add("тест ${xc}: немає inputs"); continue }
            foreach ($p in $inp.PSObject.Properties) {
                if ($kinds["$xt/$($p.Name)"] -ne 'Input') { $bad.Add("тест ${xc}: $xt/$($p.Name) не рядок вводу моделі") }
                if (-not (& $isNumber $p.Value)) { $bad.Add("тест ${xc}: ввід $($p.Name) не число") }
            }
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
