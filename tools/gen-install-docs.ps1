<#
.SYNOPSIS
    Генерує розділи інструкцій встановлення й адміністрування з джерел у репозиторії і звіряє їх (-Check).

.DESCRIPTION
    Блоки між маркерами `<!-- generated:ІМ'Я -->` … `<!-- /generated:ІМ'Я -->` (маркер — окремим рядком) у docs/**/*.md
    ПЕРЕПИСУЮТЬСЯ цим інструментом; руками їх не правити.

      grants     скрипт прав облікового запису служби. Джерело — ServiceAccountGrants (tests/Ecr.TestKit);
                 пише/звіряє сторож ServiceAccountGrantsRunbookTests (docs/admin/operations-runbook.md,
                 docs/build/11-install-guide.md, docs/delivery/QUICKSTART-FIRST-TEST.md).
      config     таблиця ключів конфігурації (ключ, обов'язковий?, типово, значення). Джерело —
                 src/Ecr.Api/Options/EcrConfigurationValidation.cs (розбір коду, константи ключів розкриваються
                 пошуком по src/**) + типові значення з src/Ecr.Api/appsettings.json. -> docs/admin/operations-runbook.md.
      sql-order  порядок розгортання SQL-скриптів. Джерело — $scripts у tools/setup-dev-db.ps1 і tools/deploy-ecr.ps1;
                 якщо перелічене в них розходиться — помилка з назвою розбіжності. -> docs/build/11-install-guide.md.
      prereqs    передумови (версії .NET, SQL Server, Windows, WiX, dotnet-ef, Node). Джерела — global.json,
                 Directory.Build.props, Directory.Packages.props, tools/deploy-ecr.ps1, tools/build-msi.ps1,
                 installer/Ecr.Installer/Package.wxs, .github/workflows/ci.yml (лише читання). -> docs/build/11-install-guide.md.

    Режими:
      (без параметрів)   переписує всі блоки.
      -Check             нічого не пише; код виходу 1, якщо будь-який блок розійшовся з джерелом (або джерела між собою).
      -SkipGrants        не чіпає блок grants (його звіряє окремий тест; потрібен сторожу, щоб не запускати dotnet test з dotnet test).
      -From <ref> -To <ref>
                         друкує нотатки оновлення (markdown) у STDOUT і НІЧОГО не пише: міграції, SQL-скрипти, нові ключі
                         конфігурації, нові права служби, змінені тексти сіду між двома ревізіями. Не комітити вивід.

.EXAMPLE
    powershell -File tools/gen-install-docs.ps1
    powershell -File tools/gen-install-docs.ps1 -Check
    powershell -File tools/gen-install-docs.ps1 -From origin/main -To HEAD
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$SkipGrants,
    [string]$From,
    [string]$To
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-Text([string]$path) {
    return [System.IO.File]::ReadAllText($path, $utf8)
}

function Join-Root([string]$relative) {
    return Join-Path $root ($relative -replace '/', [System.IO.Path]::DirectorySeparatorChar)
}

# ───────────────────────── Константи ключів із src/** ─────────────────────────

$script:Consts = New-Object System.Collections.ArrayList
$script:Enums = @{}
$script:ConstsLoaded = $false
$script:Problems = New-Object System.Collections.ArrayList

function Load-Consts {
    if ($script:ConstsLoaded) { return }
    $declRx = [regex]'(?m)\b(?:const|static\s+readonly)\s+(?:string|int)\s+(\w+)\s*=\s*([^;]+);'
    $clsRx = [regex]'\b(?:class|record|struct)\s+(\w+)'
    $enumRx = [regex]'(?s)\benum\s+(\w+)\s*(?::\s*\w+\s*)?\{(.*?)\}'
    $files = Get-ChildItem -Path (Join-Root 'src') -Recurse -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
    foreach ($file in $files) {
        $text = Read-Text $file.FullName
        if ($text -match '\benum\s+\w+') {
            foreach ($em in $enumRx.Matches($text)) {
                $body = $em.Groups[2].Value -replace '(?m)//.*$', '' -replace '\[[^\]]*\]', ''
                $members = @($body -split ',' | ForEach-Object { ($_ -replace '=.*$', '').Trim() } | Where-Object { $_ })
                $script:Enums[$em.Groups[1].Value] = $members
            }
        }
        if ($text -notmatch '(?:const|static\s+readonly)\s+(?:string|int)\s') { continue }
        $classes = $clsRx.Matches($text)
        foreach ($m in $declRx.Matches($text)) {
            $cls = ''
            foreach ($c in $classes) {
                if ($c.Index -lt $m.Index) { $cls = $c.Groups[1].Value } else { break }
            }
            [void]$script:Consts.Add(@{ Class = $cls; Name = $m.Groups[1].Value; Expr = $m.Groups[2].Value.Trim(); State = 0; Value = $null })
        }
    }
    $script:ConstsLoaded = $true
}

function Unescape-Literal([string]$s) {
    return ($s -replace '\\"', '"' -replace '\\\\', '\')
}

$script:TokenRx = [regex]'\$"(?:[^"\\]|\\.)*"|"(?:[^"\\]|\\.)*"|[A-Za-z_][\w\.]*|\d+|\+'

function Get-ConstValue($entry) {
    if ($entry.State -eq 2) { return $entry.Value }
    if ($entry.State -eq 1) { return $null }
    $entry.State = 1
    $entry.Value = Invoke-Expression-Const $entry.Expr $entry.Class
    $entry.State = 2
    return $entry.Value
}

# Значення виразу з рядкових літералів, цілих, посилань на константи, конкатенації й інтерполяції; інакше $null.
function Invoke-Expression-Const([string]$expr, [string]$cls) {
    $tokens = @($script:TokenRx.Matches($expr) | ForEach-Object { $_.Value })
    if ($tokens.Count -eq 0) { return $null }
    $sb = New-Object System.Text.StringBuilder
    foreach ($t in $tokens) {
        if ($t -eq '+') { continue }
        if ($t.StartsWith('$"')) {
            $inner = $t.Substring(2, $t.Length - 3)
            $failed = $false
            $replaced = [regex]::Replace($inner, '\{([\w\.]+)\}', {
                    param($m)
                    $v = Resolve-Ref $m.Groups[1].Value $cls
                    if ($null -eq $v) { $script:interpFailed = $true; return '' }
                    return $v
                })
            if ($script:interpFailed) { $script:interpFailed = $false; return $null }
            [void]$sb.Append((Unescape-Literal $replaced))
        }
        elseif ($t.StartsWith('"')) {
            [void]$sb.Append((Unescape-Literal $t.Substring(1, $t.Length - 2)))
        }
        elseif ($t -match '^\d+$') {
            [void]$sb.Append($t)
        }
        else {
            $v = Resolve-Ref $t $cls
            if ($null -eq $v) { return $null }
            [void]$sb.Append($v)
        }
    }
    return $sb.ToString()
}

function Resolve-Ref([string]$ref, [string]$cls) {
    Load-Consts
    $parts = $ref -split '\.'
    $name = $parts[$parts.Count - 1]
    $qualifier = $null
    if ($parts.Count -gt 1) { $qualifier = $parts[$parts.Count - 2] }
    $cands = @($script:Consts | Where-Object { $_.Name -eq $name -and ((-not $qualifier) -or $_.Class -eq $qualifier) })
    if ($cands.Count -eq 0) { return $null }
    if (-not $qualifier -and $cands.Count -gt 1) {
        $same = @($cands | Where-Object { $_.Class -eq $cls })
        if ($same.Count -gt 0) { $cands = $same }
    }
    $values = @($cands | ForEach-Object { Get-ConstValue $_ } | Where-Object { $null -ne $_ } | Select-Object -Unique)
    if ($values.Count -eq 1) { return [string]$values[0] }
    if ($values.Count -gt 1) {
        [void]$script:Problems.Add("константа '$ref' неоднозначна: " + ($values -join ' | ') + " — уточніть клас у вираженні")
    }
    return $null
}

# ───────────────────────── appsettings.json ─────────────────────────

function Add-Flat($prefix, $node, $flat) {
    foreach ($p in $node.PSObject.Properties) {
        $key = if ($prefix) { "$prefix`:$($p.Name)" } else { $p.Name }
        $v = $p.Value
        if ($v -is [System.Management.Automation.PSCustomObject]) {
            Add-Flat $key $v $flat
        }
        elseif ($v -is [System.Array]) {
            $flat[$key] = '[' + (($v | ForEach-Object { [string]$_ }) -join ', ') + ']'
        }
        elseif ($v -is [bool]) {
            $flat[$key] = $v.ToString().ToLowerInvariant()
        }
        else {
            $flat[$key] = [string]$v
        }
    }
}

function ConvertTo-FlatSettings([string]$json) {
    $flat = [ordered]@{}
    if ([string]::IsNullOrWhiteSpace($json)) { return $flat }
    Add-Flat '' (ConvertFrom-Json $json) $flat
    return $flat
}

# ───────────────────────── Розбір EcrConfigurationValidation ─────────────────────────

function Clean-Comment([string]$text) {
    $t = ($text -replace '\s+', ' ').Trim()
    $t = $t -replace '\|', '\|'
    if ($t.Length -gt 220) { $t = $t.Substring(0, 219) + '…' }
    return $t
}

function Get-ValidationRows {
    $path = Join-Root 'src/Ecr.Api/Options/EcrConfigurationValidation.cs'
    $source = Read-Text $path
    $cls = 'EcrConfigurationValidation'
    $rows = New-Object System.Collections.ArrayList
    $section = $null
    $comment = ''

    function Resolve-Key([string]$expr, [string]$where) {
        $v = Invoke-Expression-Const $expr $cls
        if ($null -eq $v) { [void]$script:Problems.Add("EcrConfigurationValidation: не вдалося розкрити ключ '$expr' ($where) — додайте розбір у tools/gen-install-docs.ps1 або спростіть вираз") }
        return $v
    }

    foreach ($raw in ($source -split "\r?\n")) {
        $line = $raw
        if ($line -match '^\s*public\s+static\s+readonly\s+IReadOnlyList<.*>\s+(Integers|Booleans|Choices)\b') {
            $section = $Matches[1]; $comment = ''; continue
        }
        if ($null -eq $section) { continue }
        if ($line -match '^\s*\];') { $section = $null; $comment = ''; continue }
        if ($line -match '^\s*$') { $comment = ''; continue }
        if ($line -match '^\s*//\s?(.*)$') { $comment = ($comment + ' ' + $Matches[1]).Trim(); continue }
        $trailing = ''
        if ($line -match '^(.*?\S)\s+//\s?([^"]*)$') { $line = $Matches[1]; $trailing = $Matches[2] }
        # ⚠ Лише коментар у ТОМУ САМОМУ рядку: коментар над записом часто описує групу з кількох ключів, і прив'язка
        # до першого з них вводила б в оману.
        $note = Clean-Comment $trailing
        $comment = ''
        if ($section -eq 'Booleans') {
            if ($line -match '^\s*(.+?),\s*$') {
                $key = Resolve-Key $Matches[1] 'Booleans'
                if ($key) { [void]$rows.Add(@{ Key = $key; Kind = 'bool'; Detail = ''; Note = $note }) }
            }
            continue
        }
        if ($line -match '^\s*\(\s*([^,]+?)\s*,\s*(.+)\)\s*,\s*$') {
            $key = Resolve-Key $Matches[1] $section
            $second = $Matches[2].Trim()
            if (-not $key) { continue }
            if ($section -eq 'Integers') {
                $min = Invoke-Expression-Const $second $cls
                if ($null -eq $min) { [void]$script:Problems.Add("EcrConfigurationValidation: не вдалося розкрити межу '$second' для $key"); continue }
                [void]$rows.Add(@{ Key = $key; Kind = 'int'; Detail = $min; Note = $note })
            }
            else {
                $allowed = @()
                if ($second -match '^\[(.*)\]$') {
                    $allowed = @([regex]::Matches($Matches[1], '"([^"]*)"') | ForEach-Object { $_.Groups[1].Value })
                    [void]$rows.Add(@{ Key = $key; Kind = 'choice'; Detail = ($allowed -join ', '); Note = $note })
                }
                elseif ($second -match 'Enum\.GetNames<([\w\.]+)>\(\)') {
                    $enumName = ($Matches[1] -split '\.')[-1]
                    if ($script:Enums.ContainsKey($enumName)) {
                        [void]$rows.Add(@{ Key = $key; Kind = 'choice'; Detail = ($script:Enums[$enumName] -join ', '); Note = $note })
                    }
                    else {
                        [void]$rows.Add(@{ Key = $key; Kind = 'choice'; Detail = "значення переліку $enumName (зовнішня бібліотека)"; Note = $note })
                    }
                }
                else {
                    [void]$script:Problems.Add("EcrConfigurationValidation: невідомий перелік допустимих значень для ${key}: $second")
                }
            }
        }
    }

    # Ключі, що перевіряються поза трьома переліками: константи *Key файлу й локальні `var xKey = Тип.КонстантаKey;`.
    $extra = New-Object System.Collections.ArrayList
    foreach ($m in [regex]::Matches($source, '\bconst\s+string\s+(\w+Key)\s*=\s*"([^"]+)"')) {
        [void]$extra.Add($m.Groups[2].Value)
    }
    foreach ($m in [regex]::Matches($source, '\bvar\s+\w+\s*=\s*([\w\.]+Key)\s*;')) {
        $v = Resolve-Key $m.Groups[1].Value 'локальна змінна'
        if ($v) { [void]$extra.Add($v) }
    }
    foreach ($key in $extra) {
        if (-not ($rows | Where-Object { $_.Key -eq $key })) {
            [void]$rows.Add(@{ Key = $key; Kind = 'other'; Detail = ''; Note = '' })
        }
    }

    # Умовно обов'язкові: «{КЛЮЧ} = «»: обов'язковий, коли {УВІМКНЕНО} = true».
    $conditional = @{}
    foreach ($m in [regex]::Matches($source, "\{(\w+Key)\} = «»: обов'язковий, коли \{([\w\.]+)\} = true")) {
        $k = Resolve-Key $m.Groups[1].Value 'умовно обов''язковий'
        $e = Resolve-Key $m.Groups[2].Value 'умовно обов''язковий'
        if ($k -and $e) { $conditional[$k] = $e }
    }
    return @{ Rows = $rows; Conditional = $conditional }
}

function New-ConfigBlock {
    $validation = Get-ValidationRows
    $flat = ConvertTo-FlatSettings (Read-Text (Join-Root 'src/Ecr.Api/appsettings.json'))
    $byKey = @{}
    foreach ($r in $validation.Rows) { $byKey[$r.Key.ToLowerInvariant()] = $r }
    $names = @{}
    foreach ($k in $flat.Keys) { $names[$k.ToLowerInvariant()] = $k }
    foreach ($r in $validation.Rows) { if (-not $names.ContainsKey($r.Key.ToLowerInvariant())) { $names[$r.Key.ToLowerInvariant()] = $r.Key } }

    $lines = New-Object System.Collections.ArrayList
    [void]$lines.Add('<!-- generated:config -->')
    [void]$lines.Add('Таблиця згенерована `tools/gen-install-docs.ps1` з `src/Ecr.Api/Options/EcrConfigurationValidation.cs` (що службі перевіряється на старті) і `src/Ecr.Api/appsettings.json` (типові значення). Руками не правити: `powershell -File tools/gen-install-docs.ps1`. «—» у «Типово» означає, що значення за замовчуванням задає код, а не `appsettings.json`. Порожнє значення будь-якого ключа — «не задано» (дефолт коду).')
    [void]$lines.Add('')
    [void]$lines.Add("| Ключ | Обов'язковий | Типово | Значення |")
    [void]$lines.Add('|---|---|---|---|')
    foreach ($lower in ($names.Keys | Sort-Object { $names[$_] } -CaseSensitive)) {
        $key = $names[$lower]
        $row = $byKey[$lower]
        $required = 'ні'
        if ($key -like 'ConnectionStrings:*') { $required = 'так (секрет: лише змінна оточення `ECR_' + ($key -replace ':', '__') + '`)' }
        foreach ($ck in $validation.Conditional.Keys) {
            if ($ck.ToLowerInvariant() -eq $lower) { $required = 'так, якщо `' + $validation.Conditional[$ck] + '` = true' }
        }
        $default = '—'
        if ($flat.Contains($key)) {
            $d = $flat[$key]
            $default = if ($d -eq '') { '*порожньо*' } else { '`' + ($d -replace '\|', '\|') + '`' }
        }
        $meaning = ''
        if ($null -eq $row) { $meaning = 'Значення на старті не перевіряється.' }
        elseif ($row.Kind -eq 'int') { $meaning = "Ціле число, не менше $($row.Detail)." }
        elseif ($row.Kind -eq 'bool') { $meaning = '`true` або `false`.' }
        elseif ($row.Kind -eq 'choice') { $meaning = 'Одне з: ' + $row.Detail + '.' }
        else { $meaning = 'Формат перевіряється на старті (див. текст відмови служби).' }
        if ($row -and $row.Note) { $meaning += ' ' + $row.Note }
        [void]$lines.Add("| ``$key`` | $required | $default | $meaning |")
    }
    [void]$lines.Add('<!-- /generated:config -->')
    return ($lines -join "`n")
}

# ───────────────────────── Порядок SQL-скриптів ─────────────────────────

function Get-SqlOrder {
    $dev = Read-Text (Join-Root 'tools/setup-dev-db.ps1')
    $deploy = Read-Text (Join-Root 'tools/deploy-ecr.ps1')

    $devBlock = [regex]::Match($dev, '(?s)\$scripts\s*=\s*@\((.*?)\r?\n\)')
    if (-not $devBlock.Success) { [void]$script:Problems.Add('sql-order: у tools/setup-dev-db.ps1 не знайдено масиву $scripts = @( … )') ; return $null }
    $devList = @([regex]::Matches($devBlock.Groups[1].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value })

    $deployBlock = [regex]::Match($deploy, '(?s)\$scripts\.AddRange\(\[string\[\]\]\((.*?)\)\)')
    if (-not $deployBlock.Success) { [void]$script:Problems.Add('sql-order: у tools/deploy-ecr.ps1 не знайдено $scripts.AddRange([string[]]( … ))'); return $null }
    $deployList = @([regex]::Matches($deployBlock.Groups[1].Value, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value })

    $optional = @()
    foreach ($m in [regex]::Matches($deploy, '(?m)if\s*\(\$(\w+)\)\s*\{\s*\$scripts\.Add\(''([^'']+)''\)')) {
        $optional += , @{ Switch = $m.Groups[1].Value; File = $m.Groups[2].Value }
    }

    if (($devList -join '|') -ne ($deployList -join '|')) {
        [void]$script:Problems.Add(
            "sql-order: порядок SQL-скриптів у tools/setup-dev-db.ps1 і tools/deploy-ecr.ps1 РОЗХОДИТЬСЯ.`n" +
            "  setup-dev-db.ps1: " + ($devList -join ', ') + "`n" +
            "  deploy-ecr.ps1:   " + ($deployList -join ', ') + "`n" +
            '  Порядок має бути однаковий (docs/build/09-commands.md §3): вирівняйте $scripts в обох файлах.')
    }

    $sqlDir = Join-Root 'src/Ecr.Infrastructure/Persistence/Sql'
    foreach ($f in ($deployList + ($optional | ForEach-Object { $_.File }))) {
        if ($f -ne '<migration>' -and -not (Test-Path (Join-Path $sqlDir $f))) {
            [void]$script:Problems.Add("sql-order: скрипт '$f' з переліку розгортання відсутній у src/Ecr.Infrastructure/Persistence/Sql.")
        }
    }
    return @{ Order = $deployList; Optional = $optional }
}

function New-SqlOrderBlock {
    $order = Get-SqlOrder
    if ($null -eq $order) { return $null }
    $lines = New-Object System.Collections.ArrayList
    [void]$lines.Add('<!-- generated:sql-order -->')
    [void]$lines.Add('Порядок розгортання SQL-скриптів згенеровано `tools/gen-install-docs.ps1` з `$scripts` у `tools/deploy-ecr.ps1` і `tools/setup-dev-db.ps1` (вони мусять збігатися). Руками не правити. Кожен файл виконується окремою транзакцією `sqlcmd`; DBA, що накочує вручну, дотримується цього порядку:')
    [void]$lines.Add('')
    $i = 1
    foreach ($f in $order.Order) {
        if ($f -eq '<migration>') { [void]$lines.Add("$i. ``migration.sql`` — міграції EF (ідемпотентний скрипт ``dotnet ef migrations script --idempotent``)") }
        else { [void]$lines.Add("$i. ``$f``") }
        $i++
    }
    foreach ($o in $order.Optional) {
        [void]$lines.Add("$i. ``$($o.File)`` — лише з ``-$($o.Switch)`` (одноразово, завдання SQL Agent у ``msdb``)")
        $i++
    }
    [void]$lines.Add('')
    [void]$lines.Add('Сід (`Sql/seed/**`) у цей перелік не входить: його виконує сам застосунок при першому старті.')
    [void]$lines.Add('<!-- /generated:sql-order -->')
    return ($lines -join "`n")
}

# ───────────────────────── Передумови ─────────────────────────

function New-PrereqsBlock {
    $global = ConvertFrom-Json (Read-Text (Join-Root 'global.json'))
    $sdk = $global.sdk.version
    $roll = $global.sdk.rollForward
    $build = Read-Text (Join-Root 'Directory.Build.props')
    $tfm = [regex]::Match($build, '<TargetFramework>([^<]+)</TargetFramework>').Groups[1].Value
    $packages = Read-Text (Join-Root 'Directory.Packages.props')
    $ef = [regex]::Match($packages, '<PackageVersion Include="Microsoft\.EntityFrameworkCore\.Design" Version="([^"]+)"').Groups[1].Value
    $deploy = Read-Text (Join-Root 'tools/deploy-ecr.ps1')
    $sqlMin = [regex]::Match($deploy, '\$major -lt (\d+) -or \(\$major -eq \d+ -and \$build -lt (\d+)\)')
    $sqlName = [regex]::Match($deploy, 'нижче мінімальної версії ([^(]+)\((\d+\.\d+\.\d+)\)')
    $msi = Read-Text (Join-Root 'tools/build-msi.ps1')
    $wix = [regex]::Match($msi, 'wix --version (\d+\.\d+\.\d+)').Groups[1].Value
    $selfContained = $msi -match '--self-contained true'
    $wxs = Read-Text (Join-Root 'installer/Ecr.Installer/Package.wxs')
    $nt = [regex]::Match($wxs, 'VersionNT64 (?:&gt;|>)= (\d+)').Groups[1].Value
    $ci = Read-Text (Join-Root '.github/workflows/ci.yml')
    $node = [regex]::Match($ci, "node-version: '(\d+)'").Groups[1].Value

    foreach ($pair in @(@('global.json sdk.version', $sdk), @('Directory.Build.props TargetFramework', $tfm), @('Directory.Packages.props EF Design', $ef),
            @('deploy-ecr.ps1 мінімум SQL', $sqlMin.Success), @('deploy-ecr.ps1 назва мінімуму SQL', $sqlName.Success), @('build-msi.ps1 версія WiX', $wix),
            @('Package.wxs VersionNT64', $nt), @('ci.yml node-version', $node))) {
        if (-not $pair[1]) { [void]$script:Problems.Add("prereqs: не вдалося прочитати '$($pair[0])' — змінився формат джерела, поправте tools/gen-install-docs.ps1") }
    }
    if ($script:Problems.Count -gt 0 -and -not $sdk) { return $null }
    $ntNames = @{ '601' = 'Windows 7 / Server 2008 R2'; '602' = 'Windows 8 / Server 2012'; '603' = 'Windows 8.1 / Server 2012 R2'; '1000' = 'Windows 10 / Server 2016' }
    $winName = if ($ntNames.ContainsKey($nt)) { $ntNames[$nt] } else { "NT $nt" }
    $sqlMinText = if ($sqlMin.Success -and $sqlName.Success) {
        "не нижче $($sqlName.Groups[1].Value.Trim()) (``$($sqlName.Groups[2].Value)``)"
    } else { '—' }
    $runtime = if ($selfContained) { 'не потрібен: публікація self-contained, рантайм усередині пакета' } else { "потрібен .NET Runtime $tfm" }

    $lines = New-Object System.Collections.ArrayList
    [void]$lines.Add('<!-- generated:prereqs -->')
    [void]$lines.Add('Версії згенеровано `tools/gen-install-docs.ps1` з репозиторію (`global.json`, `Directory.Build.props`, `Directory.Packages.props`, `tools/deploy-ecr.ps1`, `tools/build-msi.ps1`, `installer/Ecr.Installer/Package.wxs`, `.github/workflows/ci.yml`). Руками не правити.')
    [void]$lines.Add('')
    [void]$lines.Add('| Що | Де потрібне | Версія |')
    [void]$lines.Add('|---|---|---|')
    [void]$lines.Add("| .NET Runtime | сервер, куди ставиться MSI | $runtime |")
    [void]$lines.Add("| Windows | сервер, куди ставиться MSI | $winName і новіша, x64 (NT 6.$([int]$nt - 600)) |")
    [void]$lines.Add("| SQL Server | сервер БД | $sqlMinText, редакція зі SQL Server Agent (Standard і вище) |")
    [void]$lines.Add("| .NET SDK | лише машина збірки | ``$sdk`` (``rollForward``: ``$roll``), цільова платформа ``$tfm`` |")
    [void]$lines.Add("| WiX CLI | лише машина збірки | ``$wix`` |")
    [void]$lines.Add("| dotnet-ef | лише машина збірки (генерує ``migration.sql``) | ``$ef`` |")
    [void]$lines.Add("| Node.js | лише розробка й збірка клієнта; на сервері з MSI НЕ потрібен | ``$node`` |")
    [void]$lines.Add('<!-- /generated:prereqs -->')
    return ($lines -join "`n")
}

# ───────────────────────── Запис / звірка блоків у docs ─────────────────────────

$RequiredDocs = [ordered]@{
    'config'    = @('docs/admin/operations-runbook.md')
    'sql-order' = @('docs/build/11-install-guide.md')
    'prereqs'   = @('docs/build/11-install-guide.md')
}

function Sync-Blocks([bool]$checkOnly) {
    $generators = [ordered]@{
        'config'    = { New-ConfigBlock }
        'sql-order' = { New-SqlOrderBlock }
        'prereqs'   = { New-PrereqsBlock }
    }
    $docs = Get-ChildItem -Path (Join-Root 'docs') -Recurse -Filter '*.md'
    $changed = New-Object System.Collections.ArrayList
    foreach ($name in $generators.Keys) {
        $expected = & $generators[$name]
        if ($null -eq $expected) { continue }
        $startRx = '(?m)^<!-- generated:' + [regex]::Escape($name) + ' -->$'
        $endRx = '(?m)^<!-- /generated:' + [regex]::Escape($name) + ' -->$'
        $found = @()
        foreach ($doc in $docs) {
            $text = (Read-Text $doc.FullName)
            if ($text -notmatch ('generated:' + [regex]::Escape($name) + ' -->')) { continue }
            $relative = $doc.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
            $found += $relative
            $crlf = $text.Contains("`r`n")
            $normalized = $text.Replace("`r`n", "`n")
            $s = [regex]::Matches($normalized, $startRx)
            $e = [regex]::Matches($normalized, $endRx)
            if ($s.Count -ne 1 -or $e.Count -ne 1 -or $e[0].Index -lt $s[0].Index) {
                [void]$script:Problems.Add("${relative}: має бути рівно один блок <!-- generated:$name --> … <!-- /generated:$name --> (маркери — окремими рядками)")
                continue
            }
            $endPos = $e[0].Index + $e[0].Length
            $actual = $normalized.Substring($s[0].Index, $endPos - $s[0].Index)
            if ($actual -ceq $expected) { continue }
            if ($checkOnly) {
                [void]$script:Problems.Add("${relative}: блок generated:$name розійшовся з джерелами. Виправлення: powershell -File tools/gen-install-docs.ps1")
                continue
            }
            $updated = $normalized.Substring(0, $s[0].Index) + $expected + $normalized.Substring($endPos)
            if ($crlf) { $updated = $updated.Replace("`n", "`r`n") }
            [System.IO.File]::WriteAllText($doc.FullName, $updated, $utf8)
            [void]$changed.Add("$relative [$name]")
        }
        foreach ($required in $RequiredDocs[$name]) {
            if ($found -notcontains $required) {
                [void]$script:Problems.Add("${required}: немає блоку <!-- generated:$name --> … <!-- /generated:$name -->")
            }
        }
    }
    return $changed
}

# ───────────────────────── Нотатки оновлення (-From/-To) ─────────────────────────

function Invoke-Git {
    $output = & git -c core.quotepath=off -C $root @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') завершився з кодом $LASTEXITCODE" }
    return $output
}

function Show-Git([string]$ref, [string]$path) {
    # Файла в ревізії може не бути (нова фіча) — це не помилка; 2>$null у PS 5.1 при Stop став би винятком.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = & git -c core.quotepath=off -C $root show "${ref}:${path}" 2>$null }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0) { return '' }
    return ($out -join "`n")
}

function Write-UpgradeNotes([string]$from, [string]$to) {
    [Console]::OutputEncoding = $utf8
    $out = New-Object System.Collections.ArrayList
    [void]$out.Add("# Нотатки оновлення ``$from`` → ``$to``")
    [void]$out.Add('')
    [void]$out.Add('_Згенеровано `tools/gen-install-docs.ps1 -From -To`; у репозиторій не комітиться._')
    [void]$out.Add('')

    # Міграції EF.
    $migPath = 'src/Ecr.Infrastructure/Persistence/Migrations'
    $mig = @(Invoke-Git diff --name-status --no-renames $from $to -- $migPath |
            Where-Object { $_ -match '^A\s' -and $_ -notmatch '\.Designer\.cs$' -and $_ -notmatch 'ModelSnapshot' } |
            ForEach-Object { ([System.IO.Path]::GetFileNameWithoutExtension(($_ -split "\s+", 2)[1])) })
    [void]$out.Add("## Міграції EF ($($mig.Count))")
    if ($mig.Count -eq 0) { [void]$out.Add('Немає нових міграцій.') } else { foreach ($m in $mig) { [void]$out.Add("- ``$m``") } }
    [void]$out.Add('')

    # SQL-скрипти (не сід).
    $sqlPath = 'src/Ecr.Infrastructure/Persistence/Sql'
    $sql = @(Invoke-Git diff --name-status --no-renames $from $to -- $sqlPath |
            Where-Object { $_ -notmatch '/seed/' } |
            ForEach-Object { $p = $_ -split "\s+", 2; $verb = if ($p[0] -eq 'A') { 'додано' } elseif ($p[0] -eq 'D') { 'видалено' } else { 'змінено' }; "$verb ``$([System.IO.Path]::GetFileName($p[1]))``" })
    [void]$out.Add("## SQL-скрипти ($($sql.Count))")
    if ($sql.Count -eq 0) { [void]$out.Add('Скрипти не змінювались.') } else { foreach ($s in $sql) { [void]$out.Add("- $s") } }
    [void]$out.Add('')

    # Конфігурація.
    $cfgPath = 'src/Ecr.Api/appsettings.json'
    $before = ConvertTo-FlatSettings (Show-Git $from $cfgPath)
    $after = ConvertTo-FlatSettings (Show-Git $to $cfgPath)
    $added = @($after.Keys | Where-Object { -not $before.Contains($_) })
    $removed = @($before.Keys | Where-Object { -not $after.Contains($_) })
    $changedDefaults = @($after.Keys | Where-Object { $before.Contains($_) -and $before[$_] -ne $after[$_] })
    [void]$out.Add('## Конфігурація (`appsettings.json`)')
    if ($added.Count + $removed.Count + $changedDefaults.Count -eq 0) { [void]$out.Add('Без змін типових значень і ключів.') }
    foreach ($k in $added) { [void]$out.Add("- новий ключ ``$k`` (типово ``$($after[$k])``)") }
    foreach ($k in $changedDefaults) { [void]$out.Add("- змінено типове ``$k``: ``$($before[$k])`` → ``$($after[$k])``") }
    foreach ($k in $removed) { [void]$out.Add("- прибрано ключ ``$k``") }
    $valPath = 'src/Ecr.Api/Options/EcrConfigurationValidation.cs'
    $keyRx = [regex]'"([A-Z][A-Za-z]+(?::[A-Za-z]+)+)"'
    $valBefore = @($keyRx.Matches((Show-Git $from $valPath)) | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $valAfter = @($keyRx.Matches((Show-Git $to $valPath)) | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $valNew = @($valAfter | Where-Object { $valBefore -notcontains $_ })
    foreach ($k in $valNew) { [void]$out.Add("- ключ ``$k`` тепер перевіряється на старті (недійсне значення зупиняє службу)") }
    [void]$out.Add('')

    # Права служби.
    $grantsPath = 'tests/Ecr.TestKit/ServiceAccountGrants.cs'
    $listRx = [regex]'(?s)public static readonly IReadOnlyList<string> (Roles|Sequences|TableTypes|Procedures|Functions)\s*=\s*\[(.*?)\];'
    $grantList = {
        param($text)
        $set = New-Object System.Collections.ArrayList
        foreach ($m in $listRx.Matches($text)) {
            foreach ($q in [regex]::Matches($m.Groups[2].Value, '"([^"]+)"')) { [void]$set.Add($m.Groups[1].Value + ': ' + $q.Groups[1].Value) }
        }
        return @($set)
    }
    $gBefore = & $grantList (Show-Git $from $grantsPath)
    $gAfter = & $grantList (Show-Git $to $grantsPath)
    $gNew = @($gAfter | Where-Object { $gBefore -notcontains $_ })
    $gGone = @($gBefore | Where-Object { $gAfter -notcontains $_ })
    [void]$out.Add('## Права облікового запису служби')
    if ($gNew.Count + $gGone.Count -eq 0) { [void]$out.Add('Перелік прав не змінювався.') }
    else {
        foreach ($g in $gNew) { [void]$out.Add("- НОВЕ: ``$g`` — DBA має видати перед стартом нової версії (скрипт: розділ «DBA: виконати скрипт прав» інструкції встановлення)") }
        foreach ($g in $gGone) { [void]$out.Add("- прибрано: ``$g``") }
    }
    [void]$out.Add('')

    # Змінені тексти сіду: нові рядки (ключ, мова, старе, нове) у секції «Змінені тексти наявних ключів».
    $seedDiff = @(Invoke-Git diff --no-renames -U0 $from $to -- "$sqlPath/seed")
    $textRx = [regex]"^\+\s*\(N'([^']+)',\s*N'(\w+)',\s*N'.*',\s*N'.*'\)"
    $texts = @($seedDiff | ForEach-Object { $m = $textRx.Match($_); if ($m.Success) { "$($m.Groups[1].Value) [$($m.Groups[2].Value)]" } })
    [void]$out.Add("## Змінені тексти інтерфейсу ($($texts.Count))")
    if ($texts.Count -eq 0) { [void]$out.Add('Рядків «старе → нове» не додано.') }
    else {
        [void]$out.Add('Застосунок сам оновить їх на старті, якщо адміністратор не переписав текст у `/admin/ui-strings`:')
        foreach ($t in $texts) { [void]$out.Add("- ``$t``") }
    }
    Write-Output ($out -join "`n")
}

# ───────────────────────── Точка входу ─────────────────────────

if ($From -or $To) {
    if (-not ($From -and $To)) { throw 'gen-install-docs: -From і -To задаються разом.' }
    Write-UpgradeNotes $From $To
    exit 0
}

$exitCode = 0
if (-not $SkipGrants) {
    $project = Join-Root 'tests/Ecr.Architecture.Tests/Ecr.Architecture.Tests.csproj'
    $previous = $env:ECR_REGENERATE_DOCS
    $env:ECR_REGENERATE_DOCS = if ($Check) { $null } else { '1' }
    try {
        & dotnet test $project --filter 'FullyQualifiedName~ServiceAccountGrantsRunbookTests' --nologo -v q
        if ($LASTEXITCODE -ne 0) {
            if ($Check) { Write-Host 'gen-install-docs: скрипт прав розійшовся з ServiceAccountGrants.' -ForegroundColor Red; $exitCode = 1 }
            else { throw "gen-install-docs: генерація не пройшла (код $LASTEXITCODE) — дивіться вивід тесту вище." }
        }
    }
    finally {
        $env:ECR_REGENERATE_DOCS = $previous
    }
}

$updated = Sync-Blocks ([bool]$Check)
if ($script:Problems.Count -gt 0) {
    foreach ($p in $script:Problems) { Write-Host "gen-install-docs: $p" -ForegroundColor Red }
    exit 1
}
if ($exitCode -ne 0) { exit $exitCode }
if ($Check) { Write-Host 'gen-install-docs: блоки в docs збігаються з джерелами.' }
else {
    foreach ($u in $updated) { Write-Host "  оновлено $u" }
    Write-Host 'gen-install-docs: інструкції встановлення оновлено (grants, config, sql-order, prereqs).'
}
