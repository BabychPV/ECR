using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Розбиття <c>Ecr.Api.Tests</c> на частини конвеєра (<c>tools/api-test-shards.psd1</c>) не губить
/// і не дублює жодного тестового класу.
/// </summary>
/// <remarks>
/// ⛔ Частини 1..N-1 — об'єднання префіксів, остання (шард-залишок) — заперечення всіх тих
/// префіксів. Тому «випасти з усіх» формально неможливо, але одруківка в префіксі
/// (<c>Securty.D</c>) тихо переводить клас у залишок і ламає баланс, а префікс, що є початком
/// іншого, дав би клас у двох частинах. Цей сторож читає ДІЙСНІ класи з <c>tests/Ecr.Api.Tests</c>
/// і перевіряє: кожен клас — рівно в одній частині; кожен префікс щось відбирає.
/// </remarks>
public sealed partial class ApiTestShardsTests
{
    private sealed record Shard(int Id, int Part, bool Rest, string[] Prefixes);

    [GeneratedRegex(@"@\{\s*Id\s*=\s*(\d+)\s*;\s*Part\s*=\s*(\d+)\s*;[^}]*\}")]
    private static partial Regex ShardLine();

    [GeneratedRegex(@"Prefixes\s*=\s*@\(([^)]*)\)")]
    private static partial Regex PrefixList();

    [GeneratedRegex(@"'([^']+)'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"(?m)^\s*namespace\s+([A-Za-z0-9_.]+)")]
    private static partial Regex NamespaceDecl();

    [GeneratedRegex(@"\b(?:class|record)\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeDecl();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_клас_Api_Tests_потрапляє_рівно_в_одну_частину()
    {
        var shards = Shards();
        var classes = ApiTestClasses();
        Assert.True(classes.Count > 100, $"Знайдено лише {classes.Count} класів у tests/Ecr.Api.Tests — розбір зламано?");

        var lastPart = shards.Max(s => s.Part);
        var problems = new List<string>();

        foreach (var fqn in classes)
        {
            var parts = shards
                .Where(s => !s.Rest && s.Prefixes.Any(p => Matches(fqn, p)))
                .Select(s => s.Part)
                .Distinct()
                .ToList();

            // Залишок бере все, що не відібрали інші частини, — рівно за фільтром verify-all.ps1.
            var inRest = !shards.Where(s => !s.Rest).SelectMany(s => s.Prefixes).Any(p => Matches(fqn, p));
            var total = parts.Count + (inRest ? 1 : 0);

            if (total != 1)
            {
                problems.Add($"{fqn}: у частинах [{string.Join(',', parts)}]{(inRest ? $" + {lastPart} (залишок)" : string.Empty)}");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_префікс_шарда_відбирає_хоч_один_клас_і_не_є_початком_іншого()
    {
        var shards = Shards();
        var classes = ApiTestClasses();
        var prefixes = shards.Where(s => !s.Rest).SelectMany(s => s.Prefixes).ToList();

        Assert.Equal(prefixes.Count, prefixes.Distinct(StringComparer.Ordinal).Count());

        var nested = prefixes
            .SelectMany(a => prefixes.Where(b => a != b && b.StartsWith(a, StringComparison.Ordinal)).Select(b => $"'{a}' є початком '{b}'"))
            .ToList();
        Assert.Empty(nested);

        var dead = prefixes.Where(p => !classes.Any(c => Matches(c, p))).ToList();
        Assert.Empty(dead);

        Assert.Single(shards, s => s.Rest);
        Assert.Equal(shards.Max(s => s.Part), shards.Single(s => s.Rest).Part);
        Assert.All(Enumerable.Range(1, shards.Max(s => s.Part)), k => Assert.Contains(shards, s => s.Part == k));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Для_кожної_частини_є_крок_verify_all()
    {
        var script = File.ReadAllText(Path.Combine(Root(), "tools", "verify-all.ps1"));
        foreach (var part in Shards().Select(s => s.Part).Distinct())
        {
            Assert.Contains($"Step 'Тести API (частина {part})'", script, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// ⛔ Дефект 8521f457c: <c>…текст коментаря.@{</c> — відкривний <c>@{</c> опинився в кінці
    /// коментарного рядка, і <c>Import-PowerShellDataFile</c> бачив порожній файл, а Windows
    /// PowerShell 5.1 без BOM читає кирилицю як ANSI. Регекси інших тестів цього класу такого
    /// не помічають (вони шукають рядки шардів), тому окремий сторож тексту.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Файл_шардів_має_BOM_LF_і_жодного_коментаря_з_відкривним_блоком()
    {
        var bytes = File.ReadAllBytes(Path.Combine(Root(), "tools", "api-test-shards.psd1"));
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..], "api-test-shards.psd1 без BOM UTF-8: Windows PowerShell 5.1 прочитає кирилицю як ANSI.");
        Assert.DoesNotContain((byte)'\r', bytes);

        var lines = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('﻿').Split('\n');
        var bad = lines
            .Select((l, i) => (Line: l, No: i + 1))
            .Where(x => x.Line.TrimStart().StartsWith('#') && x.Line.Contains("@{", StringComparison.Ordinal))
            .Select(x => $"{x.No}: {x.Line}")
            .ToList();
        Assert.Empty(bad);

        var first = lines.First(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));
        Assert.Equal("@{", first.Trim());
        Assert.Equal("}", lines.Last(l => l.Trim().Length > 0).Trim());
    }

    /// <summary>Файл справді розбирається PowerShell як Data File (<c>Import-PowerShellDataFile</c>).</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public async Task Файл_шардів_розбирається_як_PowerShell_Data_File()
    {
        var path = Path.Combine(Root(), "tools", "api-test-shards.psd1");
        var start = new System.Diagnostics.ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
                     $"$d = Import-PowerShellDataFile -LiteralPath '{path.Replace("'", "''", StringComparison.Ordinal)}'; if ($d.Count -lt 1) {{ exit 3 }}; Write-Output ('keys=' + $d.Count)" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell не завершився за 2 хв");
        }

        var output = await stdout;
        var error = await stderr;
        Assert.True(process.ExitCode == 0, $"Import-PowerShellDataFile: код {process.ExitCode}\n{error}\n{output}");
        Assert.Contains("keys=", output, StringComparison.Ordinal);
    }

    private static bool Matches(string fqn, string prefix)
        => fqn.Contains("Ecr.Api.Tests." + prefix, StringComparison.Ordinal);

    private static List<Shard> Shards()
    {
        var text = File.ReadAllText(Path.Combine(Root(), "tools", "api-test-shards.psd1"));
        var shards = new List<Shard>();
        foreach (var line in text.Split('\n').Where(l => !l.TrimStart().StartsWith('#')))
        {
            var m = ShardLine().Match(line);
            if (!m.Success)
            {
                continue;
            }

            var prefixes = PrefixList().Match(line) is { Success: true } p
                ? Quoted().Matches(p.Groups[1].Value).Select(q => q.Groups[1].Value).ToArray()
                : Array.Empty<string>();
            shards.Add(new Shard(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),line.Contains("Rest", StringComparison.Ordinal), prefixes));
        }

        Assert.NotEmpty(shards);
        return shards;
    }

    private static List<string> ApiTestClasses()
    {
        var dir = Path.Combine(Root(), "tests", "Ecr.Api.Tests");
        var sep = Path.DirectorySeparatorChar;
        var result = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}obj{sep}", StringComparison.Ordinal) || file.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var ns = NamespaceDecl().Match(text);
            if (!ns.Success)
            {
                continue;
            }

            foreach (Match t in TypeDecl().Matches(text))
            {
                result.Add($"{ns.Groups[1].Value}.{t.Groups[1].Value}");
            }
        }

        return result.Where(c => c.StartsWith("Ecr.Api.Tests.", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
