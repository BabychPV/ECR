# Одиниці аргументів і констант у тексті скрипта методології Land (репетиція, коло 4; D-556).
# Чиста частина (без мережі й БД): Set-LandScriptUnits переписує текст, який зробив Ecr.MethodologyScriptMigration convert.
#   * аргумент рядка (`row.Density`) без одиниці в колонці шаблону -> змінна з оголошеною одиницею:
#       var u_Density : kg_per_Sm3 = row.Density;   і всі `row.Density` у виразах -> `u_Density`
#     (одиницю колонки опублікованої версії шаблону змінити не можна: UnitId структурне поле, ECR-TMPL-0409);
#   * константа `const k : one = 3;` -> `const k : <одиниця> = 3;`.
# Числа не змінюються: одиниці ECR перевіряє лише за розмірністю (множників не застосовує).

function Set-LandScriptUnits {
    <#
    .SYNOPSIS
        Додає одиниці аргументам і константам у тексті скрипта; помилка, якщо аргумент чи константу не знайдено.
    .PARAMETER Text        Текст скрипта.
    .PARAMETER Arguments   Хеш «колонка рядка -> код одиниці».
    .PARAMETER Constants   Хеш «ім'я константи -> код одиниці».
    #>
    param([Parameter(Mandatory)][string]$Text, $Arguments = @{}, $Constants = @{})

    $out = $Text
    $decls = New-Object System.Collections.Generic.List[string]
    foreach ($arg in @($Arguments.Keys | Sort-Object)) {
        $unit = [string]$Arguments[$arg]
        if ($arg -notmatch '^[A-Za-z_][A-Za-z0-9_]*$' -or $unit -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "Недопустиме ім'я: $arg / $unit" }
        $pattern = '(?<![A-Za-z0-9_])row\.' + [regex]::Escape($arg) + '(?![A-Za-z0-9_])'
        if ($out -match ($pattern + '\?')) { throw "Необов'язковий аргумент row.${arg}? не підтримується." }
        if (-not [regex]::IsMatch($out, $pattern)) { throw "Аргумент row.$arg у тексті скрипта не знайдено." }
        $out = [regex]::Replace($out, $pattern, "u_$arg")
        [void]$decls.Add("var u_$arg : $unit = row.$arg;")
    }
    foreach ($k in @($Constants.Keys | Sort-Object)) {
        $unit = [string]$Constants[$k]
        if ($k -notmatch '^[A-Za-z_][A-Za-z0-9_]*$' -or $unit -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "Недопустиме ім'я: $k / $unit" }
        $pattern = '(?m)^(const ' + [regex]::Escape($k) + ') : [A-Za-z_][A-Za-z0-9_]* = '
        if (-not [regex]::IsMatch($out, $pattern)) { throw "Константу $k з одиницею в тексті скрипта не знайдено." }
        $out = [regex]::Replace($out, $pattern, ('${1} : ' + $unit + ' = '))
    }
    if ($decls.Count -gt 0) {
        # перед першим var/output (після констант і рядкового речення)
        $m = [regex]::Match($out, '(?m)^(var|output|reject|if|foreach)\b')
        if (-not $m.Success) { throw 'У тексті скрипта немає var/output.' }
        $block = ($decls -join "`n") + "`n"
        if ($out.Contains("`r`n")) { $block = $block.Replace("`n", "`r`n") }
        $out = $out.Insert($m.Index, $block)
    }
    return $out
}

function Set-LandScriptArgumentSources {
    <#
    .SYNOPSIS
        Аргумент рядка, якого немає в частині таблиць, береться з колонки-замінника цих таблиць (як перейменування в старій системі).
    .DESCRIPTION
        `row.X` -> змінна `s_X`: `var s_X = row.X? == null ? row.Alt? : row.X?;`. Таблиця, де X є, читає X (порожня комірка = null,
        як було); таблиця без X (але з Alt) читає Alt. Необов'язкове читання `?` не падає на таблиці без колонки. Помилка,
        якщо `row.X` у тексті немає. Необов'язкове `row.X?` теж замінюється. ⚠ Обов'язкове `row.X` на таблиці без X раніше давало #ARG
        (рядок не рахувався), тепер дає null; для необов'язкового `row.X?` поведінка та сама.
    .PARAMETER Text     Текст скрипта.
    .PARAMETER Sources  Хеш «аргумент -> колонка-замінник».
    #>
    param([Parameter(Mandatory)][string]$Text, $Sources = @{})

    $out = $Text
    $decls = New-Object System.Collections.Generic.List[string]
    foreach ($arg in @($Sources.Keys | Sort-Object)) {
        $alt = [string]$Sources[$arg]
        if ($arg -notmatch '^[A-Za-z_][A-Za-z0-9_]*$' -or $alt -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw "Недопустиме ім'я: $arg / $alt" }
        $pattern = '(?<![A-Za-z0-9_])row\.' + [regex]::Escape($arg) + '(?![A-Za-z0-9_])'
        if (-not [regex]::IsMatch($out, $pattern)) { throw "Аргумент row.$arg у тексті скрипта не знайдено." }
        # і `row.X`, і необов'язкове `row.X?` (етап OptionalArguments) читаються через змінну
        $out = [regex]::Replace($out, $pattern + '\??', "s_$arg")
        [void]$decls.Add("var s_${arg} = row.${arg}? == null ? row.${alt}? : row.${arg}?;")
    }
    if ($decls.Count -gt 0) {
        $m = [regex]::Match($out, '(?m)^(var|output|reject|if|foreach)\b')
        if (-not $m.Success) { throw 'У тексті скрипта немає var/output.' }
        $block = ($decls -join "`n") + "`n"
        if ($out.Contains("`r`n")) { $block = $block.Replace("`n", "`r`n") }
        $out = $out.Insert($m.Index, $block)
    }
    return $out
}
