using System.Diagnostics;
using System.Text;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-1: перенос значень зі застарілих таблиць Land (T9a_03/06/07 -> T9a_17a/21a/21b) скриптом
/// <c>tools/land/Move-LandStaleTableValues.ps1</c>. Тести виконують справжні функції
/// <c>LandStaleValues.Lib.ps1</c> у PowerShell з підробленим Api (жодного HTTP, жодної бази) і самий скрипт - лише
/// до першого мережевого виклику (відмови loopback-guard і -ConfirmStand).
/// </summary>
public sealed class LandStaleValuesTests
{
    private static string LandDir => Path.Combine(SourceTree.Root, "tools", "land");

    private const string Harness = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $land = $args[0]; $mode = $args[1]
        . (Join-Path $land 'EcrApi.ps1')
        . (Join-Path $land 'LandStaleValues.Lib.ps1')

        $script:Waits = New-Object System.Collections.Generic.List[double]
        function Wait-LandStale { param([double]$Seconds) $script:Waits.Add($Seconds) }
        $script:Calls = New-Object System.Collections.Generic.List[string]
        $script:Patches = 0
        $script:Store = @{}
        $script:Instances = @{}
        $script:States = @{}
        $script:Throttle = 0
        $script:Ver = 0

        function Add-Table([int]$ti, [string[]]$cols, [string[]]$ro, $rows) {
            $script:Store[$ti] = @{ cols = $cols; ro = $ro; rows = [ordered]@{} }
            foreach ($k in @($rows.Keys)) { $script:Store[$ti].rows[$k] = @{ rowKey = $k; rowVersion = "v0-$k"; cells = $rows[$k] } }
        }

        function Reset-Fixture {
            $script:Store = @{}; $script:Instances = @{}; $script:States = @{}
            foreach ($d in 11, 12, 13) {
                $b = $d * 10
                $script:Instances[$d] = @(
                    @{ sheetCode = 'S9A'; tableCode = 'T9a_03'; tableInstanceId = ($b + 0); allowsDynamicRows = $true },
                    @{ sheetCode = 'S9A'; tableCode = 'T9a_17a'; tableInstanceId = ($b + 1); allowsDynamicRows = $true },
                    @{ sheetCode = 'S9A'; tableCode = 'T9a_06'; tableInstanceId = ($b + 2); allowsDynamicRows = $true },
                    @{ sheetCode = 'S9A'; tableCode = 'T9a_21a'; tableInstanceId = ($b + 3); allowsDynamicRows = $true })
                $script:States[$d] = 'Draft'
            }
            $newCols = @('A', 'B', 'C', 'D', 'RO')
            # doc 11: 5 значень: A(R1) однакове, B(R1) конфлікт, C(R1) і A(R2) переносяться, Z нерозв'язана колонка
            Add-Table 110 @('A', 'B', 'C', 'Z') @() ([ordered]@{ R1 = @{ A = 1; B = 2; C = 'secret31337' }; R2 = @{ A = 5; Z = 9 } })
            Add-Table 111 $newCols @('RO') ([ordered]@{ R1 = @{ A = 1; B = 99 } })
            Add-Table 112 @('A') @() ([ordered]@{})
            Add-Table 113 @('A') @() ([ordered]@{})
            # doc 12: A(R1) переноситься (рядка в цілі немає), RO лише для читання, B(R3) конфлікт
            Add-Table 120 @('A', 'B', 'RO') @() ([ordered]@{ R1 = @{ A = 31337; RO = 1 }; R3 = @{ B = 3 } })
            Add-Table 121 $newCols @('RO') ([ordered]@{ R3 = @{ B = 4 } })
            Add-Table 122 @('A') @() ([ordered]@{})
            Add-Table 123 @('A') @() ([ordered]@{})
            # doc 13: аркуш Submitted; T9a_06 має значення; T9a_07 є, а T9a_21b немає
            Add-Table 130 @('A') @() ([ordered]@{})
            Add-Table 131 $newCols @('RO') ([ordered]@{})
            Add-Table 132 @('A') @() ([ordered]@{ R1 = @{ A = 1 } })
            Add-Table 133 @('A') @() ([ordered]@{})
            $script:Instances[13] += @{ sheetCode = 'S9A'; tableCode = 'T9a_07'; tableInstanceId = 134; allowsDynamicRows = $true }
            Add-Table 134 @('A') @() ([ordered]@{ R1 = @{ A = 1 } })
            $script:States[13] = 'Submitted'
        }

        function ToJson($o) { return (ConvertTo-Json -InputObject $o -Depth 8 -Compress | ConvertFrom-Json) }

        function Invoke-Ecr {
            param([string]$Method, [string]$Path, $Body = $null, [hashtable]$Headers = @{})
            $script:Calls.Add("$Method $Path")
            $ok = { param($j) [pscustomobject]@{ Status = 200; Json = $j; Text = ''; Headers = @{} } }
            if ($script:Throttle -gt 0) {
                $script:Throttle--
                return [pscustomobject]@{ Status = 429; Json = $null; Text = ''; Headers = @{ 'Retry-After' = '7' } }
            }
            if ($Method -eq 'GET' -and $Path -match '^/api/v1/documents/(\d+)\?periodKey=(\d+)$') {
                $d = [int]$Matches[1]
                return (& $ok (ToJson @{ id = $d; sheets = @(@{ code = 'S9A'; state = $script:States[$d] }) }))
            }
            if ($Method -eq 'GET' -and $Path -match '^/api/v1/documents/(\d+)/tables\?periodKey=(\d+)$') {
                return (& $ok (ToJson @($script:Instances[[int]$Matches[1]])))
            }
            if ($Method -eq 'GET' -and $Path -match '^/api/v1/documents/(\d+)/tables/(\d+)\?periodKey=(\d+)$') {
                $t = $script:Store[[int]$Matches[2]]
                $cols = @($t.cols | ForEach-Object { @{ code = $_; isReadOnly = ($t.ro -contains $_) } })
                $rows = @($t.rows.Values | ForEach-Object { @{ rowKey = $_.rowKey; rowVersion = $_.rowVersion; cells = $_.cells } })
                return (& $ok (ToJson @{ tableInstanceId = [int]$Matches[2]; columns = $cols; rows = $rows }))
            }
            if ($Method -eq 'PATCH' -and $Path -match '^/api/v1/documents/(\d+)/cells$') {
                $script:Patches++
                $t = $script:Store[[int]$Body.tableInstanceId]
                foreach ($row in @($Body.rows)) {
                    $ex = $t.rows.Contains($row.rowKey)
                    if ($ex -and $row.baseVersion -ne $t.rows[$row.rowKey].rowVersion) { return [pscustomobject]@{ Status = 409; Json = $null; Text = ''; Headers = @{} } }
                    if (-not $ex -and $null -ne $row.baseVersion) { return [pscustomobject]@{ Status = 409; Json = $null; Text = ''; Headers = @{} } }
                    foreach ($c in @($row.cells)) {
                        if ($t.ro -contains $c.columnCode) { return [pscustomobject]@{ Status = 422; Json = $null; Text = ''; Headers = @{} } }
                    }
                }
                foreach ($row in @($Body.rows)) {
                    if (-not $t.rows.Contains($row.rowKey)) { $t.rows[$row.rowKey] = @{ rowKey = $row.rowKey; rowVersion = 'v0'; cells = @{} } }
                    $r = $t.rows[$row.rowKey]
                    foreach ($c in @($row.cells)) {
                        if ($null -eq $c.value -and -not $c.isEmpty) { $r.cells.Remove($c.columnCode) } else { $r.cells[$c.columnCode] = $c.value }
                    }
                    $script:Ver++
                    $r.rowVersion = "v$($script:Ver)"
                }
                return (& $ok $null)
            }
            return [pscustomobject]@{ Status = 404; Json = $null; Text = ''; Headers = @{} }
        }

        $maps = @(Read-LandStaleMap (Join-Path $land 'rename-maps\land-9a-missing-tables.json'))
        function Show($r) {
            foreach ($n in 'Documents', 'Pairs', 'Values', 'Moved', 'Overwritten', 'Same', 'Conflicts', 'ReadOnlyTarget', 'UnresolvedColumnValues', 'UnresolvedRowValues', 'UnresolvedTables', 'RowsWritten', 'RowsFailed', 'CellsCleared') {
                Write-Host ("R.$n=" + $r.$n)
            }
            Write-Host ('R.Blocked=' + @($r.Blocked).Count)
            foreach ($b in @($r.Blocked)) { Write-Host ('BLOCK=' + $b) }
            Write-Host ('PATCHES=' + $script:Patches)
        }
        $quiet = { param($m) }
        $loud = { param($m) Write-Host $m }

        switch ($mode) {
            'parse' {
                foreach ($f in 'Move-LandStaleTableValues.ps1', 'LandStaleValues.Lib.ps1') {
                    $errs = $null
                    [void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $land $f), [ref]$null, [ref]$errs)
                    Write-Host ("PARSE_ERRORS $f=" + @($errs).Count)
                }
            }
            'map' {
                Write-Host ('MAP_COUNT=' + $maps.Count)
                foreach ($m in $maps) { Write-Host ('MAP=' + $m.From + '>' + $m.To + '|cols=' + $m.Columns.Count) }
                $tmp = Join-Path $env:TEMP ('ecr-stale-map-' + [guid]::NewGuid().ToString('N') + '.json')
                [IO.File]::WriteAllText($tmp, '[{"fromTableCode":"A","toTableCode":"B","sheetCode":"S","columns":[{"fromColumnCode":"X","toColumnCode":"Y"}]}]')
                $m2 = @(Read-LandStaleMap $tmp)
                Remove-Item -LiteralPath $tmp
                Write-Host ('MAP2=' + $m2[0].From + '>' + $m2[0].To + '@' + $m2[0].Sheet + '|' + $m2[0].Columns['X'])
            }
            'colmap' {
                $src = ToJson @{ rows = @(@{ rowKey = 'R'; rowVersion = 'v'; cells = @{ X = 5 } }) }
                $dst = ToJson @{ columns = @(@{ code = 'Y'; isReadOnly = $false }); rows = @() }
                $p = Get-LandStaleTablePlan -Source $src -Target $dst -ColumnMap @{ X = 'Y' }
                Write-Host ('COLMAP moved=' + $p.Moved + ' unresolved=' + $p.UnresolvedColumnValues)
                $p0 = Get-LandStaleTablePlan -Source $src -Target $dst
                Write-Host ('NOMAP moved=' + $p0.Moved + ' unresolved=' + $p0.UnresolvedColumnValues + ' cols=' + ($p0.UnresolvedColumns -join ','))
                $pf = Get-LandStaleTablePlan -Source (ToJson @{ rows = @(@{ rowKey = 'R'; rowVersion = 'v'; cells = @{ Y = 5 } }) }) -Target $dst -AllowsDynamicRows $false
                Write-Host ('FIXED moved=' + $pf.Moved + ' unresolvedRows=' + $pf.UnresolvedRowValues)
            }
            'plan' {
                Reset-Fixture
                $r = Invoke-LandStaleMove -DocumentId 11, 12, 13 -PeriodKey 202606 -Maps $maps -PaceSeconds 2.2 -Log $loud
                Show $r
                $mut = @($script:Calls | Where-Object { -not $_.StartsWith('GET ') }).Count
                Write-Host ('MUTATING_CALLS=' + $mut)
                Write-Host ('WAITS=' + $script:Waits.Count + ' MINWAIT=' + (($script:Waits | Measure-Object -Minimum).Minimum))
                Write-Host ('CALLS=' + $script:Calls.Count)
            }
            'exec' {
                Reset-Fixture
                $r1 = Invoke-LandStaleMove -DocumentId 11, 12 -PeriodKey 202606 -Maps $maps -Execute -PaceSeconds 0 -Log $loud
                Write-Host '--FIRST'
                Show $r1
                $p1 = $script:Patches
                $r2 = Invoke-LandStaleMove -DocumentId 11, 12 -PeriodKey 202606 -Maps $maps -Execute -PaceSeconds 0 -Log $quiet
                Write-Host '--SECOND'
                Show $r2
                Write-Host ('PATCHES_AFTER_SECOND=' + $script:Patches)
                Write-Host ('PATCHES_AFTER_FIRST=' + $p1)
                Write-Host ('TARGET_R1=' + (($script:Store[111].rows['R1'].cells.Keys | Sort-Object) -join ','))
                Write-Host ('TARGET_B=' + $script:Store[111].rows['R1'].cells['B'])
                Write-Host ('SOURCE_R1=' + (($script:Store[110].rows['R1'].cells.Keys | Sort-Object) -join ','))
            }
            'overwrite' {
                Reset-Fixture
                $r = Invoke-LandStaleMove -DocumentId 11 -PeriodKey 202606 -Maps $maps -Execute -OverwriteConflicts -PaceSeconds 0 -Log $quiet
                Show $r
                Write-Host ('TARGET_B=' + $script:Store[111].rows['R1'].cells['B'])
            }
            'blocked' {
                Reset-Fixture
                try {
                    [void](Invoke-LandStaleMove -DocumentId 13 -PeriodKey 202606 -Maps $maps -Execute -PaceSeconds 0 -Log $quiet)
                    Write-Host 'NOT_THROWN'
                }
                catch { Write-Host ('THROWN=' + $_.Exception.Message) }
                Write-Host ('PATCHES=' + $script:Patches)
            }
            'clear' {
                Reset-Fixture
                $r = Invoke-LandStaleMove -DocumentId 11 -PeriodKey 202606 -Maps $maps -Execute -ClearSource -PaceSeconds 0 -Log $quiet
                Show $r
                Write-Host ('SRC_R1=' + (($script:Store[110].rows['R1'].cells.Keys | Sort-Object) -join ','))
                Write-Host ('SRC_R2=' + (($script:Store[110].rows['R2'].cells.Keys | Sort-Object) -join ','))
            }
            'retry' {
                Reset-Fixture
                $script:Throttle = 1
                $r = Invoke-LandStaleMove -DocumentId 11 -PeriodKey 202606 -Maps $maps -PaceSeconds 0 -Log $quiet
                Write-Host ('WAITS=' + (($script:Waits | ForEach-Object { [string]$_ }) -join ','))
                Write-Host ('R.Pairs=' + $r.Pairs)
            }
        }
        """;

    private static (int ExitCode, string Output) RunPowerShell(string scriptText, params string[] arguments)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-stale-values-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, scriptText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }
                         .Concat(arguments))
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(180_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("PowerShell не завершився за 180 с.");
            }

            return (process.ExitCode, stdout.Result + stderr.Result);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string Run(string mode)
    {
        var (code, output) = RunPowerShell(Harness, LandDir, mode);
        Assert.True(code == 0, output);
        return output;
    }

    private static string RunScript(params string[] arguments)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // -Command із UTF-8 виводом: кирилиця повідомлень не залежить від кодової сторінки консолі.
        var quoted = string.Join(' ', arguments.Select(
            a => a.StartsWith('-') && a.All(c => char.IsLetter(c) || c == '-') ? a : "'" + a.Replace("'", "''", StringComparison.Ordinal) + "'"));
        var script = Path.Combine(LandDir, "Move-LandStaleTableValues.ps1").Replace("'", "''", StringComparison.Ordinal);
        foreach (var argument in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                     $"[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false); & '{script}' {quoted}",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell не завершився за 120 с.");
        }

        Assert.NotEqual(0, process.ExitCode);
        return stdout.Result + stderr.Result;
    }

    private static int Counter(string output, string name)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(name + "=", StringComparison.Ordinal))
            {
                return int.Parse(line[(name.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        throw new Xunit.Sdk.XunitException($"Немає рядка {name}= у виводі:\n{output}");
    }

    private static string Line(string output, string prefix)
        => output.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_і_бібліотека_розбираються_парсером_без_помилок()
    {
        var output = Run("parse");

        Assert.Contains("PARSE_ERRORS Move-LandStaleTableValues.ps1=0", output, StringComparison.Ordinal);
        Assert.Contains("PARSE_ERRORS LandStaleValues.Lib.ps1=0", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Мапа_читається_з_JSON_таблиці_кодами_колонки_за_записом()
    {
        var output = Run("map");

        Assert.Equal(3, Counter(output, "MAP_COUNT"));
        Assert.Contains("MAP=T9a_03>T9a_17a|cols=0", output, StringComparison.Ordinal);
        Assert.Contains("MAP=T9a_06>T9a_21a|cols=0", output, StringComparison.Ordinal);
        Assert.Contains("MAP=T9a_07>T9a_21b|cols=0", output, StringComparison.Ordinal);
        Assert.Contains("MAP2=A>B@S|Y", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Колонка_йде_за_мапою_або_кодом_а_рядок_у_фіксованій_таблиці_не_створюється()
    {
        var output = Run("colmap");

        Assert.Contains("COLMAP moved=1 unresolved=0", output, StringComparison.Ordinal);
        Assert.Contains("NOMAP moved=0 unresolved=1 cols=X", output, StringComparison.Ordinal);
        Assert.Contains("FIXED moved=0 unresolvedRows=1", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_dry_run_на_синтетиці_рахує_перенесення_конфлікти_нерозв_язане_і_нічого_не_пише()
    {
        var output = Run("plan");

        Assert.Equal(3, Counter(output, "R.Documents"));
        Assert.Equal(3, Counter(output, "R.Pairs"));
        Assert.Equal(9, Counter(output, "R.Values"));
        Assert.Equal(4, Counter(output, "R.Moved"));
        Assert.Equal(1, Counter(output, "R.Same"));
        Assert.Equal(2, Counter(output, "R.Conflicts"));
        Assert.Equal(1, Counter(output, "R.ReadOnlyTarget"));
        Assert.Equal(1, Counter(output, "R.UnresolvedColumnValues"));
        Assert.Equal(1, Counter(output, "R.UnresolvedTables"));
        Assert.Equal(0, Counter(output, "R.RowsWritten"));
        Assert.Equal(0, Counter(output, "MUTATING_CALLS"));
        Assert.Contains("нерозв'язані колонки: Z", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_не_друкує_значень_комірок()
    {
        var output = Run("plan") + Run("exec");

        Assert.DoesNotContain("31337", output, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Аркуш_Submitted_у_плані_потребує_Reopen_з_правильним_повідомленням()
    {
        var output = Run("plan");

        Assert.Equal(1, Counter(output, "R.Blocked"));
        Assert.Contains("BLOCK=потрібен Reopen аркуша S9A/період 202606", output, StringComparison.Ordinal);
        Assert.Contains("виконує людина/координатор", Line(output, "BLOCK="), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Execute_на_Submitted_аркуші_зупиняється_до_будь_якого_запису()
    {
        var output = Run("blocked");

        Assert.Contains("THROWN=Запис не розпочато: потрібен Reopen аркуша S9A/період 202606", output, StringComparison.Ordinal);
        Assert.Equal(0, Counter(output, "PATCHES"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Execute_переносить_значення_конфлікт_не_чіпає_а_повтор_дає_нуль_змін()
    {
        var output = Run("exec");
        var parts = output.Split("--SECOND", StringSplitOptions.None);
        var first = parts[0];
        var second = parts[1];

        // doc 11: рядки R1 (C) і R2 (A); doc 12: рядок R1 (A) - три рядки двома PATCH (по одному на таблицю).
        Assert.Equal(3, Counter(first, "R.RowsWritten"));
        Assert.Equal(0, Counter(first, "R.RowsFailed"));
        Assert.Equal(2, Counter(first, "PATCHES"));
        Assert.Contains("TARGET_R1=A,B,C", output, StringComparison.Ordinal);
        Assert.Contains("TARGET_B=99", output, StringComparison.Ordinal);
        // джерело за замовчуванням не змінюється
        Assert.Contains("SOURCE_R1=A,B,C", output, StringComparison.Ordinal);

        Assert.Equal(0, Counter(second, "R.Moved"));
        Assert.Equal(0, Counter(second, "R.RowsWritten"));
        Assert.Equal(2, Counter(second, "PATCHES_AFTER_SECOND"));
        Assert.Equal(4, Counter(second, "R.Same"));
        Assert.Equal(2, Counter(second, "R.Conflicts"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Конфлікт_перезаписується_лише_з_OverwriteConflicts()
    {
        var output = Run("overwrite");

        Assert.Equal(1, Counter(output, "R.Overwritten"));
        Assert.Contains("TARGET_B=2", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void ClearSource_стирає_лише_перенесене_й_однакове_а_конфлікт_і_нерозв_язане_лишає()
    {
        var output = Run("clear");

        Assert.Contains("SRC_R1=B", output, StringComparison.Ordinal);
        Assert.Contains("SRC_R2=Z", output, StringComparison.Ordinal);
        Assert.Equal(3, Counter(output, "R.CellsCleared"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Пейсинг_тримає_паузу_між_викликами_а_Retry_After_поважається()
    {
        var plan = Run("plan");
        var waits = Line(plan, "WAITS=");
        var minWait = double.Parse(
            waits[(waits.IndexOf("MINWAIT=", StringComparison.Ordinal) + "MINWAIT=".Length)..],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(minWait > 1.5, $"Пауза між викликами менша за пейсинг: {waits}");

        var retry = Run("retry");
        Assert.Contains("WAITS=7", retry, StringComparison.Ordinal);
        Assert.Equal(1, Counter(retry, "R.Pairs"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Loopback_guard_відхиляє_нелокальні_адреси_до_будь_якого_виклику()
    {
        var external = RunScript("-ApiBase", "http://10.20.30.40:5094", "-DocumentId", "1", "-PeriodKey", "202606");
        Assert.Contains("не локальна", external, StringComparison.Ordinal);

        var hostile = RunScript("-ApiBase", "https://localhost.example.com", "-DocumentId", "1", "-PeriodKey", "202606");
        Assert.Contains("не локальна", hostile, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Запис_без_ConfirmStand_або_з_чужим_підтвердженням_відхиляється()
    {
        var none = RunScript("-ApiBase", "http://localhost:5094", "-DocumentId", "1", "-PeriodKey", "202606", "-Execute");
        Assert.Contains("-ConfirmStand", none, StringComparison.Ordinal);

        var wrong = RunScript(
            "-ApiBase", "http://localhost:5094", "-DocumentId", "1", "-PeriodKey", "202606", "-Execute", "-ConfirmStand", "localhost:9999");
        Assert.Contains("не збігається", wrong, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_за_замовчуванням_лише_план_і_не_містить_видалення_джерела_без_прапора()
    {
        var script = File.ReadAllText(Path.Combine(LandDir, "Move-LandStaleTableValues.ps1"));
        var lib = File.ReadAllText(Path.Combine(LandDir, "LandStaleValues.Lib.ps1"));

        Assert.Contains("$exec = ($Execute.IsPresent -and -not $DryRun.IsPresent)", script, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalBaseUrl $ApiBase", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$ClearSource", script, StringComparison.Ordinal);
        Assert.Contains("if ($ClearSource)", lib, StringComparison.Ordinal);
        Assert.DoesNotContain("/reopen", lib, StringComparison.Ordinal);
        Assert.DoesNotContain("/reopen", script, StringComparison.Ordinal);
    }
}
