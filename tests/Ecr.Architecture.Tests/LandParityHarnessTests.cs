using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Паритет розрахунків Land: інструменти tools/land/Compare-LandParity.ps1 і Export-LandParityOracle.ps1 працюють
/// із РЕАЛЬНИМИ значеннями прода (лише локально). Сторожі: (а) результат не пишеться в репозиторій, (б) експортер
/// лише читає (SELECT під READ UNCOMMITTED), (в) у репозиторії немає файлів із прод-значеннями, шляхів до
/// локальних вивантажень і псевдонімів, (г) логіка класів розбіжностей перевірена на СИНТЕТИЧНІЙ фікстурі.
/// </summary>
public sealed class LandParityHarnessTests
{
    private static readonly string[] PowerShellPrefix = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"];

    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private static (int Exit, string Output) RunPowerShell(params string[] args)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in PowerShellPrefix.Concat(args))
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("PowerShell не запустився");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell не завершився за 120 с.");
        }

        return (process.ExitCode, stdout + stderr);
    }

    private static string StripComments(string script)
    {
        var noBlock = Regex.Replace(script, @"<#.*?#>", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"(?m)^\s*#.*$", string.Empty);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Експортер_еталона_лише_читає_і_не_пише_у_репозиторій()
    {
        var text = File.ReadAllText(LandFile("Export-LandParityOracle.ps1"));
        var code = StripComments(text);

        Assert.Contains("READ UNCOMMITTED", code, StringComparison.Ordinal);
        Assert.Contains("only SELECT", code, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(@"if \(\$sql -notmatch '\^\\s\*SELECT\\b'\)", code);
        Assert.Contains("is not local", code, StringComparison.Ordinal);
        Assert.Contains("inside the repository", code, StringComparison.Ordinal);

        // У рядках SQL немає записуючих/DDL-слів.
        foreach (var m in Regex.Matches(code, "\"(?<s>[^\"]*)\"").Cast<Match>())
        {
            Assert.DoesNotMatch(@"(?i)\b(INSERT\s+INTO|UPDATE\s+\S+\s+SET|DELETE\s+FROM|DROP\s|TRUNCATE\s|ALTER\s|CREATE\s|EXEC(UTE)?\s|MERGE\s)", m.Groups["s"].Value);
        }

        // Усі звернення до прода йдуть через NOLOCK.
        var selects = Regex.Count(code, @"FROM dbo\.\[\$\w+\]");
        var nolock = Regex.Count(code, @"FROM dbo\.\[\$\w+\] WITH \(NOLOCK\)");
        Assert.True(selects > 0 && selects == nolock, "Кожен SELECT із dbo має бути WITH (NOLOCK).");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Компаратор_і_експортер_відмовляють_писати_результат_у_репозиторій()
    {
        var inside = Path.Combine(SourceTree.Root, "parity-out.json");
        var (exit, output) = RunPowerShell(LandFile("Export-LandParityOracle.ps1"), "-OutFile", inside);
        Assert.NotEqual(0, exit);
        Assert.Contains("inside the repository", output, StringComparison.Ordinal);
        Assert.False(File.Exists(inside));

        var dir = Directory.CreateTempSubdirectory("ecr-parity-guard-");
        try
        {
            var oracle = Path.Combine(dir.FullName, "o.json");
            File.WriteAllText(oracle, "{}");
            var (exit2, output2) = RunPowerShell(
                LandFile("Compare-LandParity.ps1"), "-JsonDir", dir.FullName, "-OracleFile", oracle, "-OutFile", inside);
            Assert.NotEqual(0, exit2);
            Assert.Contains("inside the repository", output2, StringComparison.Ordinal);
            Assert.False(File.Exists(inside));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Компаратор_на_синтетичній_фікстурі_дає_очікувані_класи_розбіжностей()
    {
        var dir = Directory.CreateTempSubdirectory("ecr-parity-fixture-");
        try
        {
            const string table = "AF_Air_Land_Stationary_Equipment";
            var sample = new
            {
                doc = "LAND-PROD-9",
                month = 1,
                year = 2026,
                tables = new Dictionary<string, object>
                {
                    [table] = new
                    {
                        rows = new object[]
                        {
                            new { Land_No = 1, Land_Status = "Running", H = 10.0 },
                            new { Land_No = 2, Land_Status = "Running", H = 20.0 },
                            new { Land_No = 3, Land_Status = "Running", H = 30.0 },
                            new { Land_No = 4, Land_Status = "Running", H = 40.0 },
                        },
                    },
                },
            };
            object LegacyRow(int seq, double h, double tons) => new
            {
                seq,
                naeAmbiguous = false,
                inputs = new { Land_No = seq, Land_Status = "Running", H = h },
                results = new[]
                {
                    new { component = "301", phase = "", tons, gsec = (double?)null, methodology = "ECW_C05_02", modified = "2026-01-01T00:00:00", source = "x" },
                },
            };
            var oracle = new
            {
                formatVersion = 1,
                documents = new Dictionary<string, object>
                {
                    ["LAND-PROD-9"] = new
                    {
                        month = 1,
                        tables = new Dictionary<string, object>
                        {
                            [table] = new
                            {
                                rows = new[] { LegacyRow(1, 10, 0.001), LegacyRow(2, 20, 0.5), LegacyRow(3, 30, 0.2), LegacyRow(4, 40, 1.0) },
                            },
                        },
                    },
                },
            };
            object Ecr(string row, string output, string value) => new
            {
                document = "LAND-PROD-9",
                methodology = "ECW_C05_02",
                rowKey = row,
                outputCode = output,
                value,
            };
            var results = new[]
            {
                Ecr("p9-0001", "ECW_EC_tons_301", "0.001"),     // збіг
                Ecr("p9-0001", "ECW_EC_tons_NOx", "0.00125"),   // проміжне: у старій системі не зберігається
                Ecr("p9-0002", "ECW_EC_tons_301", "0"),         // ECR нуль, старе ненульове
                Ecr("p9-0004", "ECW_EC_tons_301", "1000"),      // співвідношення x1000
                // p9-0003: рядок без жодного результату ECR, методологія в документі працює
            };

            var json = new JsonSerializerOptions();
            File.WriteAllText(Path.Combine(dir.FullName, "LAND-PROD-9.json"), JsonSerializer.Serialize(sample, json));
            var oracleFile = Path.Combine(dir.FullName, "oracle.json");
            var ecrFile = Path.Combine(dir.FullName, "ecr.json");
            var outFile = Path.Combine(dir.FullName, "out.json");
            File.WriteAllText(oracleFile, JsonSerializer.Serialize(oracle, json));
            File.WriteAllText(ecrFile, JsonSerializer.Serialize(results, json));

            var (exit, output) = RunPowerShell(
                LandFile("Compare-LandParity.ps1"),
                "-JsonDir", dir.FullName, "-OracleFile", oracleFile, "-EcrResultsFile", ecrFile,
                "-Documents", "LAND-PROD-9", "-OutFile", outFile);
            Assert.True(exit == 0, output);

            using var doc = JsonDocument.Parse(File.ReadAllText(outFile));
            var classes = doc.RootElement.GetProperty("classes").EnumerateArray()
                .ToDictionary(e => e.GetProperty("class").GetString()!, e => e.GetProperty("cells").GetInt32());

            Assert.Equal(1, classes["MATCH"]);
            Assert.Equal(1, classes["ECR_ONLY_INTERMEDIATE"]);
            Assert.Equal(1, classes["MISMATCH_ECR_ZERO"]);
            Assert.Equal(1, classes["MISMATCH_RATIO_x1000"]);
            Assert.Equal(1, classes["MISSING_ROW"]);
            Assert.False(classes.ContainsKey("NOT_ALIGNED"));
            // Значення не потрапляють у файл без -IncludeValues.
            Assert.DoesNotContain("\"old\"", File.ReadAllText(outFile), StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Репозиторій_не_містить_прод_вивантажень_шляхів_і_псевдонімів_паритету()
    {
        var forbiddenFiles = new[] { "LAND-PROD-*.json", "parity-oracle*.json", "parity-result*.json", "pseudonyms.local.json", "*.local.json" };
        var skip = new[] { Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar, Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar };
        foreach (var pattern in forbiddenFiles)
        {
            var hits = Directory.EnumerateFiles(SourceTree.Root, pattern, SearchOption.AllDirectories)
                .Where(f => !skip.Any(s => f.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.True(hits.Count == 0, $"Файли виду {pattern} не мають бути в репозиторії: {string.Join(", ", hits.Take(3))}");
        }

        // Скрипти паритету не знають локальних шляхів вивантаження: усе приходить параметрами.
        foreach (var name in new[] { "Compare-LandParity.ps1", "Export-LandParityOracle.ps1" })
        {
            var text = File.ReadAllText(LandFile(name));
            Assert.DoesNotContain("ecr-backup", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prod-sample", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pseudonyms", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
