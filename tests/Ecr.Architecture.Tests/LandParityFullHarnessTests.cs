using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Паритет Land на ПОВНОМУ наборі: tools/land/Compare-LandParityFull.ps1 + LandParity.Lib.ps1 +
/// Export-LandParityOracleFull.ps1. Сторожі: (а) експортер лише читає, (б) вихід і еталон не в репозиторії,
/// (в) розбір параметрів, (г) класи на СИНТЕТИЧНІЙ фікстурі без даних прода (MATCH, MATCH_ROUNDING_LOSS,
/// MISMATCH_LEGACY_ZERO, ROW_NOT_LOADED_CAPPED, MISSING_NOT_PUBLISHED, AMBIGUOUS_KEY і його агрегатне
/// порівняння), (д) у файлах виходу значень немає.
/// </summary>
public sealed class LandParityFullHarnessTests
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
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("PowerShell не завершився за 120 с.");
        }

        return (process.ExitCode, stdoutTask.Result + stderrTask.Result);
    }

    private static string StripComments(string script)
    {
        var noBlock = Regex.Replace(script, @"<#.*?#>", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlock, @"(?m)^\s*#.*$", string.Empty);
    }

    private const string Table = "AF_Air_Land_Stationary_Equipment";
    private const string Doc = "LAND-PROD-9";

    // Сентинели «значень»: не повинні з'явитися у файлах виходу.
    private const string SentinelCapped = "0.987654321";
    private const string SentinelMismatch = "0.7777777";

    private static Dictionary<string, object> Row(int no, string nae, string efid, string status = "Running") =>
        new() { ["_efid"] = efid, ["Land_No"] = no, ["Land_NAE"] = nae, ["Land_Status"] = status, ["Land_TypeFuel"] = "Diesel" };

    private static string TsvLine(string efid, int month, string nae, string tons, string methodology) =>
        string.Join('\t', efid, month, Table, nae, "301", "Diesel", tons, string.Empty, methodology, "2026-01-01T00:00:00", "CalcResult_Emissions_Air_Land_X");

    private sealed class Fixture : IDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("ecr-parity-full-");

        public string Json => Dir.FullName;

        public string Oracle => Path.Combine(Dir.FullName, "oracle.local.tsv");

        public string State => Path.Combine(Dir.FullName, "state.json");

        public string Prefix(string name) => Path.Combine(Dir.FullName, name);

        public void Dispose() => Dir.Delete(recursive: true);
    }

    private static Fixture BuildFixture()
    {
        var f = new Fixture();
        var columns = new[] { "Land_No", "Land_NAE", "Land_Status", "Land_TypeFuel" };
        var sample = new
        {
            doc = Doc,
            periods = new Dictionary<string, object>
            {
                ["2026-01"] = new Dictionary<string, object>
                {
                    [Table] = new
                    {
                        columns,
                        rows = new[]
                        {
                            Row(1, "N1", "E1"),   // збіг
                            Row(2, "N2", "E1"),   // збіг з втратою точності зберігання
                            Row(3, "N6", "E1"),   // стара = 0, ECR != 0
                            Row(4, "N3", "E1"),   // рядок не завантажено (ліміт шаблону)
                            Row(5, "N9", "E1"),   // методологія не опублікована
                            Row(6, "N10", "E1"),  // методологія без прив'язки до таблиці: потрібен -MethodologyTable
                        },
                    },
                },
                ["2026-02"] = new Dictionary<string, object>
                {
                    [Table] = new
                    {
                        columns,
                        rows = new[]
                        {
                            Row(1, "N7", "E2"), Row(2, "N7", "E2"),   // двозначний ключ: сума 1.0 = результат старої
                            Row(3, "N8", "E2"), Row(4, "N8", "E2"),   // двозначний ключ: сума не збігається
                        },
                    },
                },
            },
        };
        File.WriteAllText(Path.Combine(f.Dir.FullName, Doc + ".json"), JsonSerializer.Serialize(sample));

        File.WriteAllLines(f.Oracle,
        [
            TsvLine("E1", 1, "N1", "0.001", "ECW_C05_02"),
            TsvLine("E1", 1, "N2", "0.0000000100003", "ECW_C05_02"),
            TsvLine("E1", 1, "N6", "0", "ECW_C05_02"),
            TsvLine("E1", 1, "N3", SentinelCapped, "ECW_C05_02"),
            TsvLine("E1", 1, "N9", "3", "ECW_C11_01"),
            TsvLine("E1", 1, "N10", "2", "ECW_C99_01"),
            TsvLine("E2", 2, "N7", "1", "ECW_C05_02"),
            TsvLine("E2", 2, "N8", "1", "ECW_C05_02"),
        ]);

        static object Res(int period, string meth, string row, string output, string value) =>
            new { document = Doc, period, methodology = meth, rowKey = row, outputCode = output, value };
        var state = new
        {
            methodologies = new object[]
            {
                new { code = "ECW_C05_02", published = 1, bound = 1, tables = new[] { "T3_00" } },
                new { code = "ECW_C11_01", published = 0, bound = 0, tables = Array.Empty<string>() },
                new { code = "ECW_C99_01", published = 1, bound = 1, tables = Array.Empty<string>() },
            },
            tables = new object[]
            {
                new { document = Doc, period = 202601, table = "T3_00", rowKeys = new[] { "p9-0001", "p9-0002", "p9-0003", "p9-0005", "p9-0006" }, maxRows = 5 },
                new { document = Doc, period = 202602, table = "T3_00", rowKeys = new[] { "p9-0001", "p9-0002", "p9-0003", "p9-0004" }, maxRows = 5 },
            },
            results = new[]
            {
                Res(202601, "ECW_C05_02", "p9-0001", "ECW_EC_tons_301", "0.001"),
                Res(202601, "ECW_C05_02", "p9-0001", "ECW_EC_tons_NOx", "0.002"),
                Res(202601, "ECW_C05_02", "p9-0002", "ECW_EC_tons_301", "0.00000001"),
                Res(202601, "ECW_C05_02", "p9-0003", "ECW_EC_tons_301", SentinelMismatch),
                Res(202601, "ECW_C99_01", "p9-0006", "ECW_EC_tons_301", "2"),
                Res(202602, "ECW_C05_02", "p9-0001", "ECW_EC_tons_301", "0.4"),
                Res(202602, "ECW_C05_02", "p9-0002", "ECW_EC_tons_301", "0.6"),
                Res(202602, "ECW_C05_02", "p9-0003", "ECW_EC_tons_301", "0.1"),
                Res(202602, "ECW_C05_02", "p9-0004", "ECW_EC_tons_301", "0.1"),
            },
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));
        return f;
    }

    private static (JsonElement Root, string SummaryText, string CellsText) RunCompare(Fixture f, string name, params string[] extra)
    {
        var prefix = f.Prefix(name);
        var args = new List<string>
        {
            LandFile("Compare-LandParityFull.ps1"), "-JsonDir", f.Json, "-OracleTsv", f.Oracle, "-StateFile", f.State,
            "-OutPrefix", prefix,
        };
        args.AddRange(extra);
        var (exit, output) = RunPowerShell([.. args]);
        Assert.True(exit == 0, output);
        var summary = File.ReadAllText(prefix + ".summary.json");
        var cells = File.ReadAllText(prefix + ".cells.tsv");
        return (JsonDocument.Parse(summary).RootElement.Clone(), summary, cells);
    }

    private static Dictionary<string, int> Classes(JsonElement root) =>
        root.GetProperty("classes").EnumerateArray()
            .ToDictionary(e => e.GetProperty("class").GetString()!, e => e.GetProperty("cells").GetInt32());

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повний_експортер_еталона_лише_читає_і_не_пише_у_репозиторій()
    {
        var code = StripComments(File.ReadAllText(LandFile("Export-LandParityOracleFull.ps1")));

        Assert.Contains("READ UNCOMMITTED", code, StringComparison.Ordinal);
        Assert.Matches(@"if \(\$sql -notmatch '\^\\s\*SELECT\\b'\)", code);
        Assert.Contains("is not local", code, StringComparison.Ordinal);
        Assert.Contains("inside the repository", code, StringComparison.Ordinal);
        foreach (var m in Regex.Matches(code, "\"(?<s>[^\"]*)\"").Cast<Match>())
        {
            Assert.DoesNotMatch(@"(?i)\b(INSERT\s+INTO|UPDATE\s+\S+\s+SET|DELETE\s+FROM|DROP\s|TRUNCATE\s|ALTER\s|CREATE\s|EXEC(UTE)?\s|MERGE\s)", m.Groups["s"].Value);
        }

        var selects = Regex.Count(code, @"FROM dbo\.\[\$\w+\]");
        var nolock = Regex.Count(code, @"FROM dbo\.\[\$\w+\] WITH \(NOLOCK\)");
        Assert.True(selects > 0 && selects == nolock, "Кожен SELECT із dbo має бути WITH (NOLOCK).");

        var inside = Path.Combine(SourceTree.Root, "parity-oracle-full.tsv");
        var (exit, output) = RunPowerShell(LandFile("Export-LandParityOracleFull.ps1"), "-OutFile", inside);
        Assert.NotEqual(0, exit);
        Assert.Contains("inside the repository", output, StringComparison.Ordinal);
        Assert.False(File.Exists(inside));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повний_компаратор_відмовляє_писати_вихід_у_репозиторій()
    {
        using var f = BuildFixture();
        var inside = Path.Combine(SourceTree.Root, "parity-full-guard");
        var (exit, output) = RunPowerShell(
            LandFile("Compare-LandParityFull.ps1"), "-JsonDir", f.Json, "-OracleTsv", f.Oracle, "-StateFile", f.State, "-OutPrefix", inside);
        Assert.NotEqual(0, exit);
        Assert.Contains("inside the repository", output, StringComparison.Ordinal);
        Assert.False(File.Exists(inside + ".summary.json"));
        Assert.False(File.Exists(inside + ".cells.tsv"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Розбір_параметрів_GroupBy_MethodologyTable_AggregateBy()
    {
        var (exit, output) = RunPowerShell(
            LandFile("Compare-LandParityFull.ps1"), "-ValidateOnly",
            "-AggregateBy", "NaePhase", "-Months", "3,4", "-GroupBy", "T11_02=Land_TypeFuel+Land_Grp;Land_Alt",
            "-MethodologyTable", "^ECW_C12_01$=T11_02+T11a_01;^ECW_C08_04=T6_11");
        Assert.True(exit == 0, output);
        var json = output.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith('{'));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("NaePhase", root.GetProperty("aggregateBy").GetString());
        Assert.Equal([3, 4], root.GetProperty("months").EnumerateArray().Select(m => m.GetInt32()).ToArray());   // "3,4" - два місяці, не 34
        var groupBy = root.GetProperty("groupBy").EnumerateArray()
            .ToDictionary(e => e.GetProperty("table").GetString()!, e => e.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.Equal(["Land_TypeFuel", "Land_Grp"], groupBy["T11_02"]);
        Assert.Equal(["Land_Alt"], groupBy["*"]);
        var map = root.GetProperty("methodologyTable").EnumerateArray().ToList();
        Assert.Equal(2, map.Count);
        Assert.Equal("^ECW_C12_01$", map[0].GetProperty("regex").GetString());
        Assert.Equal(["T11_02", "T11a_01"], map[0].GetProperty("tables").EnumerateArray().Select(t => t.GetString()!).ToArray());

        foreach (var bad in new[]
        {
            new[] { "-MethodologyTable", "no-equals-sign" },
            new[] { "-MethodologyTable", "^ECW_C12_01$=NotATable" },
            new[] { "-GroupBy", "T11_02=bad column" },
            new[] { "-AggregateBy", "Bogus" },
            new[] { "-Months", "13" },
        })
        {
            var (e2, o2) = RunPowerShell([LandFile("Compare-LandParityFull.ps1"), "-ValidateOnly", .. bad]);
            Assert.NotEqual(0, e2);
            Assert.False(string.IsNullOrWhiteSpace(o2));
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Повний_компаратор_на_синтетичній_фікстурі_дає_очікувані_класи()
    {
        using var f = BuildFixture();
        var (root, summary, cells) = RunCompare(f, "default", "-MethodologyTable", "^ECW_C99_01$=T3_00");
        var classes = Classes(root);

        Assert.Equal(2, classes["MATCH"]);                     // N1 + N10 (через -MethodologyTable)
        Assert.Equal(1, classes["MATCH_ROUNDING_LOSS"]);       // N2
        Assert.Equal(1, classes["MISMATCH_LEGACY_ZERO"]);      // N6
        Assert.Equal(1, classes["ROW_NOT_LOADED_CAPPED"]);     // N3
        Assert.Equal(1, classes["MISSING_NOT_PUBLISHED"]);     // N9
        Assert.Equal(1, classes["ECR_ONLY_INTERMEDIATE"]);
        Assert.Equal(12, classes["AMBIGUOUS_KEY"]);            // 4 рядки x (tons + gsec старої + вихід ECR) - як у harness зразка
        Assert.Equal(0, root.GetProperty("aggregateGroups").GetInt32());

        // Зведення по методологіях і загальне — без значень.
        Assert.Equal(classes.Values.Sum(), root.GetProperty("totalCells").GetInt32());
        var byMeth = root.GetProperty("byMethodology").EnumerateArray()
            .ToDictionary(e => e.GetProperty("methodology").GetString()!, e => e);
        Assert.True(byMeth.ContainsKey("ECW_C05_02") && byMeth.ContainsKey("ECW_C11_01") && byMeth.ContainsKey("ECW_C99_01"));

        // Значення прода не потрапляють у вихід.
        foreach (var text in new[] { summary, cells })
        {
            Assert.DoesNotContain(SentinelCapped, text, StringComparison.Ordinal);
            Assert.DoesNotContain(SentinelMismatch, text, StringComparison.Ordinal);
            Assert.DoesNotContain("0.0000000100003", text, StringComparison.Ordinal);
        }

        Assert.StartsWith("doc\tperiod\ttable\trowKey\trows\tefid\tmethodology\toutput\tclass\trel", cells, StringComparison.Ordinal);
        Assert.Contains("p9-0004\t", cells, StringComparison.Ordinal);       // рядок не завантажено
    }

    /// <summary>
    /// D-425: вихід, якого немає в легасі, у групі з рядком не-Running — це <c>ECR_NON_RUNNING</c> (регресія фільтра
    /// «лише Running», індикатор має бути 0), а не <c>ECR_ONLY</c>; для групи з одних Running — як і раніше
    /// <c>ECR_ONLY</c>. Мутація: прибрати підміну класу в LandParity.Lib.ps1 — перший випадок червоніє.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("Removed", "AGG_ECR_NON_RUNNING", "AGG_ECR_ONLY", 1)]
    [InlineData("Running", "AGG_ECR_ONLY", "AGG_ECR_NON_RUNNING", 0)]
    public void Вихід_ECR_у_групі_з_рядком_не_Running_має_клас_ECR_NON_RUNNING(
        string secondRowStatus, string expected, string absent, int nonRunningCells)
    {
        using var f = new Fixture();
        var columns = new[] { "Land_No", "Land_NAE", "Land_Status", "Land_TypeFuel" };
        var sample = new
        {
            doc = Doc,
            periods = new Dictionary<string, object>
            {
                ["2026-01"] = new Dictionary<string, object>
                {
                    [Table] = new { columns, rows = new[] { Row(1, "N1", "E1"), Row(2, "N1", "E1", secondRowStatus) } },
                },
            },
        };
        File.WriteAllText(Path.Combine(f.Dir.FullName, Doc + ".json"), JsonSerializer.Serialize(sample));
        File.WriteAllLines(f.Oracle, [TsvLine("E1", 1, "N1", "0.001", "ECW_C05_02")]);

        static object Res(string row, string output, string value) =>
            new { document = Doc, period = 202601, methodology = "ECW_C05_02", rowKey = row, outputCode = output, value };
        var state = new
        {
            methodologies = new object[] { new { code = "ECW_C05_02", published = 1, bound = 1, tables = new[] { "T3_00" } } },
            tables = new object[]
            {
                new { document = Doc, period = 202601, table = "T3_00", rowKeys = new[] { "p9-0001", "p9-0002" }, maxRows = 5 },
            },
            results = new[]
            {
                Res("p9-0001", "ECW_EC_tons_301", "0.0006"),
                Res("p9-0002", "ECW_EC_tons_301", "0.0004"),
                Res("p9-0002", "ECW_EC_tons_302", "0.5"),
            },
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));

        var (root, _, _) = RunCompare(f, "nonrunning", "-AggregateBy", "NaePhase");
        var classes = Classes(root);

        Assert.Equal(1, classes[expected]);
        Assert.False(classes.ContainsKey(absent));
        Assert.Equal(nonRunningCells, root.GetProperty("ecrNonRunningCells").GetInt32());
    }

    /// <summary>
    /// Діагностика post-K4 C12: LEGACY_STATUS_SKIP (легасі пропускає не-Running рядок) - пояснений пропуск, а не
    /// «oracle-only». Лічильник <c>explainedSkip</c> окремий; <c>oracleOnly</c> лишається 0. Мутація: повернути
    /// LEGACY_STATUS_SKIP у групу ORACLE_ONLY (Get-LandParityBucket) - тест червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void LEGACY_STATUS_SKIP_іде_в_explainedSkip_а_не_в_oracleOnly()
    {
        using var f = new Fixture();
        var columns = new[] { "Land_No", "Land_NAE", "Land_Status", "Land_TypeFuel" };
        var sample = new
        {
            doc = Doc,
            periods = new Dictionary<string, object>
            {
                ["2026-01"] = new Dictionary<string, object>
                {
                    [Table] = new { columns, rows = new[] { Row(1, "N1", "E1", "Removed"), Row(2, "N2", "E1") } },
                },
            },
        };
        File.WriteAllText(Path.Combine(f.Dir.FullName, Doc + ".json"), JsonSerializer.Serialize(sample));
        File.WriteAllLines(f.Oracle, [TsvLine("E1", 1, "N1", "0.001", "ECW_C05_02"), TsvLine("E1", 1, "N2", "0.002", "ECW_C05_02")]);
        var state = new
        {
            methodologies = new object[] { new { code = "ECW_C05_02", published = 1, bound = 1, tables = new[] { "T3_00" } } },
            tables = new object[] { new { document = Doc, period = 202601, table = "T3_00", rowKeys = new[] { "p9-0001", "p9-0002" }, maxRows = 5 } },
            results = new[] { new { document = Doc, period = 202601, methodology = "ECW_C05_02", rowKey = "p9-0002", outputCode = "ECW_EC_tons_301", value = "0.002" } },
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));

        var (root, _, _) = RunCompare(f, "explained");

        Assert.Equal(1, Classes(root)["LEGACY_STATUS_SKIP"]);
        Assert.Equal(4, root.GetProperty("formatVersion").GetInt32());
        var b = root.GetProperty("buckets").EnumerateArray().Single(e => e.GetProperty("methodology").GetString() == "ECW_C05_02");
        Assert.Equal(0, b.GetProperty("oracleOnly").GetInt32());
        Assert.Equal(1, b.GetProperty("explainedSkip").GetInt32());
        Assert.Equal(1, b.GetProperty("matched").GetInt32());
        var sub = root.GetProperty("bySubstance").EnumerateArray().Single(e => e.GetProperty("substance").GetString() == "301");
        Assert.Equal(1, sub.GetProperty("explainedSkip").GetInt32());
        var header = File.ReadLines(f.Prefix("explained") + ".buckets.tsv").First();
        Assert.Contains("oracleOnly\texplainedSkip\tother", header, StringComparison.Ordinal);
    }

    /// <summary>
    /// -GroupBy з відсутньою у таблиці колонкою: усі рядки в одній групі (AMBIGUOUS_KEY) - мовчки не можна.
    /// Warning у summary.warnings (з підказкою кандидатів, без підміни); -StrictGroupBy - ненульовий код виходу.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Відсутня_колонка_GroupBy_дає_warning_а_StrictGroupBy_помилку()
    {
        using var f = BuildFixture();

        var (okRoot, _, _) = RunCompare(f, "gb-ok", "-GroupBy", "T3_00=Land_TypeFuel");
        Assert.Empty(okRoot.GetProperty("warnings").EnumerateArray());

        var (root, _, _) = RunCompare(f, "gb-warn", "-GroupBy", "T3_00=Land_Streams");
        var warnings = root.GetProperty("warnings").EnumerateArray().Select(e => e.GetString()!).ToList();
        var w = Assert.Single(warnings);
        Assert.Contains("Land_Streams", w, StringComparison.Ordinal);
        Assert.Contains("T3_00", w, StringComparison.Ordinal);
        Assert.Contains("Land_TypeFuel", w, StringComparison.Ordinal);    // підказка, не підміна
        Assert.True(Classes(root).ContainsKey("AMBIGUOUS_KEY"));          // колонку НЕ підмінено мовчки

        var (exit, _) = RunPowerShell(LandFile("Compare-LandParityFull.ps1"), "-JsonDir", f.Json, "-OracleTsv", f.Oracle,
            "-StateFile", f.State, "-OutPrefix", f.Prefix("gb-strict"), "-GroupBy", "T3_00=Land_Streams", "-StrictGroupBy");
        Assert.NotEqual(0, exit);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Агрегатне_порівняння_двозначного_ключа_сумує_групу()
    {
        using var f = BuildFixture();
        var (root, _, _) = RunCompare(f, "agg", "-AggregateBy", "NaePhase", "-MethodologyTable", "^ECW_C99_01$=T3_00");
        var classes = Classes(root);

        Assert.False(classes.ContainsKey("AMBIGUOUS_KEY"));
        Assert.Equal(1, classes["AGG_MATCH"]);       // 0.4 + 0.6 = результат старої
        Assert.Equal(1, classes["AGG_MISMATCH"]);    // 0.1 + 0.1 != результат старої
        Assert.Equal(2, root.GetProperty("aggregateGroups").GetInt32());
        // Рядкові класи не змінилися.
        Assert.Equal(2, classes["MATCH"]);
        Assert.Equal(1, classes["ROW_NOT_LOADED_CAPPED"]);

        // Без -MethodologyTable методологія без прив'язки не має таблиці: N10 уже не збіг.
        var (root2, _, _) = RunCompare(f, "nomap");
        var classes2 = Classes(root2);
        Assert.Equal(1, classes2["MATCH"]);
        Assert.Equal(1, classes2["MISSING_BOUND_NO_RESULT"]);
    }

    private static Fixture BuildSingleRowFixture(string secondRowStatus = "Running", bool twoRows = false)
    {
        var f = new Fixture();
        var columns = new[] { "Land_No", "Land_NAE", "Land_Status", "Land_TypeFuel" };
        var rows = twoRows
            ? new[] { Row(1, "N1", "E1"), Row(2, "N1", "E1", secondRowStatus) }
            : new[] { Row(1, "N1", "E1") };
        var sample = new
        {
            doc = Doc,
            periods = new Dictionary<string, object>
            {
                ["2026-01"] = new Dictionary<string, object> { [Table] = new { columns, rows } },
            },
        };
        File.WriteAllText(Path.Combine(f.Dir.FullName, Doc + ".json"), JsonSerializer.Serialize(sample));
        return f;
    }

    /// <summary>
    /// K1-діагностика п.B: легасі-ключ <c>p&lt;doc&gt;-&lt;seq&gt;</c> без коду таблиці колізує між таблицями документа
    /// (обидві гілки type1/type2 з різних таблиць потрапляли в один ключ -> BRANCH_BOTH_PRESENT). Результат із
    /// TableInstanceId (поле <c>table</c>) належить рівно своїй таблиці. Мутація: прибрати використання TableCode в
    /// Add-LandParityEcrResult - другий випадок червоніє.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(false)]
    [InlineData(true)]
    public void Результат_із_TableInstanceId_не_колізує_між_таблицями_з_однаковим_rowKey(bool withTableInstance)
    {
        using var f = BuildSingleRowFixture();
        File.WriteAllLines(f.Oracle, [TsvLine("E1", 1, "N1", "0.001", "ECW_C12_02")]);

        static object Res(string output, string value, string? table) =>
            new { document = Doc, period = 202601, methodology = "ECW_C12_02", rowKey = "p9-0001", outputCode = output, value, table };
        var state = new
        {
            methodologies = new object[] { new { code = "ECW_C12_02", published = 1, bound = 1, anyBinding = 1, tables = new[] { "T3_00", "T3_01" } } },
            tables = new object[]
            {
                new { document = Doc, period = 202601, table = "T3_00", rowKeys = new[] { "p9-0001" }, maxRows = 5 },
                new { document = Doc, period = 202601, table = "T3_01", rowKeys = new[] { "p9-0001" }, maxRows = 5 },
            },
            results = new[]
            {
                Res("ECW_EC_tons_301_type1", "0.001", withTableInstance ? "T3_00" : null),
                Res("ECW_EC_tons_301_type2", "0.5", withTableInstance ? "T3_01" : null),
            },
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));

        var (root, _, _) = RunCompare(f, "tableinst");
        var classes = Classes(root);

        if (withTableInstance)
        {
            Assert.False(classes.ContainsKey("BRANCH_BOTH_PRESENT"));
            Assert.Equal(1, classes["MATCH"]);
            Assert.Equal(2, root.GetProperty("resultsWithTableInstance").GetInt32());
            Assert.Equal(0, root.GetProperty("resultsWithoutTableInstance").GetInt32());
        }
        else
        {
            Assert.Equal(1, classes["BRANCH_BOTH_PRESENT"]);
            Assert.Equal(0, root.GetProperty("resultsWithTableInstance").GetInt32());
            Assert.Equal(2, root.GetProperty("resultsWithoutTableInstance").GetInt32());
        }
    }

    /// <summary>
    /// Селектор гілки (C11_01): легасі рахує ОДНУ гілку за NAE/потоком (ECW_C11_01.cs:183-208), ECR - усі чотири на одному
    /// ключі. З селектором вибрана гілка порівнюється (MATCH), решта - ECR_ONLY_NOT_SELECTED; без селектора
    /// (-NoBranchSelector) - BRANCH_BOTH_PRESENT. Мутація: вимкнути селектор (BranchSelector = @()) - перший вимір
    /// червоніє (замість MATCH - BRANCH_BOTH_PRESENT); підмінити вибір гілки - MISMATCH.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("6648", "type2", false)]
    [InlineData("0100", "type1_2", false)]
    [InlineData("6648", "type2", true)]
    public void Селектор_гілки_C11_01_порівнює_лише_обрану_гілку(string nae, string selected, bool noSelector)
    {
        using var f = BuildSingleRowFixture();
        // Рядок фікстури має Land_NAE = N1; для селектора потрібен власний NAE.
        var sample = new
        {
            doc = Doc,
            periods = new Dictionary<string, object>
            {
                ["2026-01"] = new Dictionary<string, object>
                {
                    [Table] = new { columns = new[] { "Land_No", "Land_NAE", "Land_Status", "Land_TypeFuel" }, rows = new[] { Row(1, nae, "E1") } },
                },
            },
        };
        File.WriteAllText(Path.Combine(f.Dir.FullName, Doc + ".json"), JsonSerializer.Serialize(sample));

        var values = new Dictionary<string, string> { ["type1_1"] = "0.1", ["type1_2"] = "0.2", ["type1_3"] = "0.3", ["type2"] = "0.5" };
        File.WriteAllLines(f.Oracle, [TsvLine("E1", 1, nae, values[selected], "ECW_C11_01")]);

        var state = new
        {
            methodologies = new object[] { new { code = "ECW_C11_01", published = 1, bound = 1, anyBinding = 1, tables = new[] { "T3_00" } } },
            tables = new object[] { new { document = Doc, period = 202601, table = "T3_00", rowKeys = new[] { "p9-0001" }, maxRows = 5 } },
            results = values.Select(kv => new
            {
                document = Doc,
                period = 202601,
                methodology = "ECW_C11_01",
                rowKey = "p9-0001",
                outputCode = "ECW_EC_tons_301_" + kv.Key,
                value = kv.Value,
            }).ToArray(),
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));

        var (root, _, _) = noSelector ? RunCompare(f, "branch", "-NoBranchSelector", "-TypeFoldMethodology", "^ECW_C11_01$") : RunCompare(f, "branch");
        var classes = Classes(root);

        if (noSelector)
        {
            Assert.Equal(1, classes["BRANCH_BOTH_PRESENT"]);
            Assert.False(classes.ContainsKey("ECR_ONLY_NOT_SELECTED"));
            Assert.Equal(0, root.GetProperty("branchSelectedCells").GetInt32());
        }
        else
        {
            Assert.Equal(1, classes["MATCH"]);
            Assert.Equal(3, classes["ECR_ONLY_NOT_SELECTED"]);
            Assert.False(classes.ContainsKey("BRANCH_BOTH_PRESENT"));
            Assert.False(classes.ContainsKey("ECR_ONLY"));
            Assert.Equal(1, root.GetProperty("branchSelectedCells").GetInt32());
            Assert.Equal(3, root.GetProperty("notSelectedCells").GetInt32());
        }
    }

    private static Dictionary<string, object> BranchRow(int no, string nae, string stream = "") =>
        new()
        {
            ["_efid"] = "E1",
            ["Land_No"] = no,
            ["Land_NAE"] = nae,
            ["Land_Status"] = "Running",
            ["Land_TypeFuel"] = "Diesel",
            ["Land_TypeFuelOrStream"] = stream,
        };

    private static string BranchOracle(string nae, string tons, string gsec, string methodology, string comp = "301") =>
        string.Join('\t', "E1", 1, Table, nae, comp, "Diesel", tons, gsec, methodology, "2026-01-01T00:00:00", "CalcResult_Emissions_Air_Land_X");

    /// <summary>Один прогін селектора: рядки (rowKey p9-000i за порядком), еталон, результати ECR (рядок, вихід, значення).</summary>
    private static (JsonElement Root, string Cells) RunBranchCase(
        string methodology, Dictionary<string, object>[] rows, string[] oracle, (int Row, string Output, string Value)[] results, params string[] extra)
    {
        using var f = new Fixture();
        var columns = new[] { "Land_No", "Land_NAE", "Land_Status", "Land_TypeFuel", "Land_TypeFuelOrStream" };
        var sample = new
        {
            doc = Doc,
            periods = new Dictionary<string, object> { ["2026-01"] = new Dictionary<string, object> { [Table] = new { columns, rows } } },
        };
        File.WriteAllText(Path.Combine(f.Dir.FullName, Doc + ".json"), JsonSerializer.Serialize(sample));
        File.WriteAllLines(f.Oracle, oracle);
        var state = new
        {
            methodologies = new object[] { new { code = methodology, published = 1, bound = 1, anyBinding = 1, tables = new[] { "T3_00" } } },
            tables = new object[]
            {
                new { document = Doc, period = 202601, table = "T3_00", rowKeys = Enumerable.Range(1, rows.Length).Select(i => $"p9-{i:D4}").ToArray(), maxRows = 20 },
            },
            results = results.Select(r => new
            {
                document = Doc,
                period = 202601,
                methodology,
                rowKey = $"p9-{r.Row:D4}",
                outputCode = r.Output,
                value = r.Value,
            }).ToArray(),
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));
        var (root, _, cells) = RunCompare(f, "branchcase", extra);
        return (root, cells);
    }

    /// <summary>C11_02: вибір лише за NAE (точний збіг без пробілів); NAE поза списком (у т.ч. «6583 (002)») - результату легасі немає.</summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Селектор_C11_02_обирає_гілку_за_NAE_а_невідомий_NAE_дає_MISSING_OUTPUT()
    {
        var (root, _) = RunBranchCase("ECW_C11_02",
            [BranchRow(1, "6640"), BranchRow(2, "6783(001)"), BranchRow(3, "6583 (002)"), BranchRow(4, "9999")],
            [
                BranchOracle("6640", "1", "", "ECW_C11_02"),
                BranchOracle("6783(001)", "2", "", "ECW_C11_02"),
                BranchOracle("6583 (002)", "1", "", "ECW_C11_02"),
                BranchOracle("9999", "1", "", "ECW_C11_02"),
            ],
            Enumerable.Range(1, 4).SelectMany(i => new[] { (i, "ECW_EC_tons_301_type1", "1"), (i, "ECW_EC_tons_301_type2", "2") }).ToArray());
        var classes = Classes(root);

        Assert.Equal(2, classes["MATCH"]);
        Assert.Equal(2, classes["MISSING_OUTPUT"]);
        Assert.False(classes.ContainsKey("ECR_ONLY"));
        Assert.DoesNotContain(classes.Keys, k => k.Contains("MISMATCH", StringComparison.Ordinal));
        Assert.Equal(2, root.GetProperty("branchSelectedCells").GetInt32());
        Assert.Equal(6, root.GetProperty("notSelectedCells").GetInt32());
    }

    /// <summary>C11_11_03: на одному рядку tons і gsec мають різні групи: NAE 0804 - tons=type3, gsec=type2.</summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Селектор_C11_11_03_NAE_0804_дає_tons_type3_і_gsec_type2_на_одному_рядку()
    {
        var (root, _) = RunBranchCase("ECW_C11_11_03",
            [BranchRow(1, "0804")],
            [BranchOracle("0804", "3", "20", "ECW_C11_11_03")],
            [
                (1, "ECW_EC_tons_301_type1", "1"), (1, "ECW_EC_tons_301_type2", "2"), (1, "ECW_EC_tons_301_type3", "3"),
                (1, "ECW_EC_gsec_301_type1", "10"), (1, "ECW_EC_gsec_301_type2", "20"), (1, "ECW_EC_gsec_301_type3", "30"),
            ]);
        var classes = Classes(root);

        Assert.Equal(2, classes["MATCH"]);
        Assert.False(classes.ContainsKey("ECR_ONLY"));
        Assert.DoesNotContain(classes.Keys, k => k.Contains("MISMATCH", StringComparison.Ordinal));
        Assert.Equal(2, root.GetProperty("branchSelectedCells").GetInt32());
        Assert.Equal(4, root.GetProperty("notSelectedCells").GetInt32());
    }

    /// <summary>C11_11_02: 6976 і 0883 - type2, 0560 - type1 (NAE має бути в groupNAE).</summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Селектор_C11_11_02_6976_і_0883_type2_а_0560_type1()
    {
        var (root, _) = RunBranchCase("ECW_C11_11_02",
            [BranchRow(1, "6976"), BranchRow(2, "0883"), BranchRow(3, "0560")],
            [
                BranchOracle("6976", "2", "", "ECW_C11_11_02"),
                BranchOracle("0883", "2", "", "ECW_C11_11_02"),
                BranchOracle("0560", "1", "", "ECW_C11_11_02"),
            ],
            Enumerable.Range(1, 3).SelectMany(i => new[] { (i, "ECW_EC_tons_301_type1", "1"), (i, "ECW_EC_tons_301_type2", "2") }).ToArray());
        var classes = Classes(root);

        Assert.Equal(3, classes["MATCH"]);
        Assert.DoesNotContain(classes.Keys, k => k.Contains("MISMATCH", StringComparison.Ordinal));
        Assert.Equal(3, root.GetProperty("branchSelectedCells").GetInt32());
        Assert.Equal(3, root.GetProperty("notSelectedCells").GetInt32());
    }

    /// <summary>C11_01: ECR віддав лише НЕобрану гілку (NAE 6648 -> type2, а є type1_3): MISSING_OUTPUT + ECR_ONLY_NOT_SELECTED, не ECR_ONLY.</summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Селектор_C11_01_лише_невибрана_гілка_ECR_дає_MISSING_OUTPUT()
    {
        var (root, _) = RunBranchCase("ECW_C11_01",
            [BranchRow(1, "6648", "Diesel")],
            [BranchOracle("6648", "0.5", "", "ECW_C11_01")],
            [(1, "ECW_EC_tons_301_type1_3", "0.3")]);
        var classes = Classes(root);

        Assert.Equal(1, classes["MISSING_OUTPUT"]);
        Assert.Equal(1, classes["ECR_ONLY_NOT_SELECTED"]);
        Assert.False(classes.ContainsKey("ECR_ONLY"));
        Assert.False(classes.ContainsKey("BRANCH_BOTH_PRESENT"));
        Assert.Equal(0, root.GetProperty("branchSelectedCells").GetInt32());
    }

    /// <summary>
    /// P2: у групі efid|NAE|phase (-AggregateBy NaePhase) гілку обирає КОЖЕН член за власним потоком: gas -> type1_1,
    /// Diesel -> type1_2; сума вибраних = результат легасі. Мутація: брати гілку з першого члена групи
    /// (Row = $info.Row замість $rowByKey[$mk]) - тест червоніє (AGG_MISMATCH).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Селектор_C11_01_у_групі_обирає_гілку_по_кожному_члену()
    {
        var (root, _) = RunBranchCase("ECW_C11_01",
            [BranchRow(1, "0100", "gas"), BranchRow(2, "0100", "Diesel")],
            [BranchOracle("0100", "0.3", "", "ECW_C11_01")],
            [
                (1, "ECW_EC_tons_301_type1_1", "0.1"), (1, "ECW_EC_tons_301_type1_2", "9"),
                (2, "ECW_EC_tons_301_type1_1", "9"), (2, "ECW_EC_tons_301_type1_2", "0.2"),
            ],
            "-AggregateBy", "NaePhase");
        var classes = Classes(root);

        Assert.Equal(1, root.GetProperty("aggregateGroups").GetInt32());
        Assert.Equal(1, classes["AGG_MATCH"]);
        Assert.DoesNotContain(classes.Keys, k => k.Contains("MISMATCH", StringComparison.Ordinal));
    }

    /// <summary>C11_01: потік «gas, Diesel» - пізніший збіг (Diesel) перемагає -> type1_2; ECR-only вибрана гілка не рахується як порівняна.</summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Селектор_C11_01_потік_gas_і_Diesel_дає_type1_2_а_ECR_only_не_рахується_порівняною()
    {
        var (root, _) = RunBranchCase("ECW_C11_01",
            [BranchRow(1, "0100", "gas, Diesel")],
            [BranchOracle("0100", "0.2", "", "ECW_C11_01")],
            [
                (1, "ECW_EC_tons_301_type1_1", "0.1"), (1, "ECW_EC_tons_301_type1_2", "0.2"),
                (1, "ECW_EC_tons_302_type1_2", "0.5"),   // вибрана гілка, але легасі виходу 302 не має
            ]);
        var classes = Classes(root);

        Assert.Equal(1, classes["MATCH"]);
        Assert.Equal(1, classes["ECR_ONLY"]);
        Assert.Equal(1, root.GetProperty("branchSelectedCells").GetInt32());   // лише порівняна клітинка
    }
    /// <summary>
    /// Проміжні виходи SumXiDiv* (C11_11_02) - не вихід легасі: ECR_ONLY_INTERMEDIATE, а не ECR_ONLY.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Проміжний_вихід_SumXiDiv_має_клас_ECR_ONLY_INTERMEDIATE()
    {
        using var f = BuildSingleRowFixture();
        File.WriteAllLines(f.Oracle, [TsvLine("E1", 1, "N1", "0.001", "ECW_C05_02")]);
        static object Res(string output, string value) =>
            new { document = Doc, period = 202601, methodology = "ECW_C05_02", rowKey = "p9-0001", outputCode = output, value };
        var state = new
        {
            methodologies = new object[] { new { code = "ECW_C05_02", published = 1, bound = 1, anyBinding = 1, tables = new[] { "T3_00" } } },
            tables = new object[] { new { document = Doc, period = 202601, table = "T3_00", rowKeys = new[] { "p9-0001" }, maxRows = 5 } },
            results = new[] { Res("ECW_EC_tons_301", "0.001"), Res("ECW_EC_tons_SumXiDivMi", "0.7") },
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));

        var classes = Classes(RunCompare(f, "interm").Root);

        Assert.Equal(1, classes["MATCH"]);
        Assert.Equal(1, classes["ECR_ONLY_INTERMEDIATE"]);
        Assert.False(classes.ContainsKey("ECR_ONLY"));
    }

    /// <summary>
    /// P3: Published методологія з прив'язками, усі IsActive=0 -> MISSING_BINDING_INACTIVE (а не MISSING_NO_BINDING);
    /// справжня відсутність прив'язки лишається MISSING_NO_BINDING.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData(1, "MISSING_BINDING_INACTIVE", "MISSING_NO_BINDING", 1)]
    [InlineData(0, "MISSING_NO_BINDING", "MISSING_BINDING_INACTIVE", 0)]
    public void Опублікована_методологія_з_вимкненими_прив_язками_має_окремий_клас(
        int anyBinding, string expected, string absent, int inactiveCells)
    {
        using var f = BuildSingleRowFixture();
        File.WriteAllLines(f.Oracle, [TsvLine("E1", 1, "N1", "0.001", "ECW_C05_02")]);
        var state = new
        {
            methodologies = new object[] { new { code = "ECW_C05_02", published = 1, bound = 0, anyBinding, tables = new[] { "T3_00" } } },
            tables = new object[] { new { document = Doc, period = 202601, table = "T3_00", rowKeys = new[] { "p9-0001" }, maxRows = 5 } },
            results = Array.Empty<object>(),
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));

        var (root, _, _) = RunCompare(f, "inactive");
        var classes = Classes(root);

        Assert.Equal(1, classes[expected]);
        Assert.False(classes.ContainsKey(absent));
        Assert.Equal(inactiveCells, root.GetProperty("missingBindingInactiveCells").GetInt32());
    }

    /// <summary>
    /// Діагностика п.A: ECR_NON_RUNNING - лише коли результат є на не-Running рядку; групи, де не-Running рядок
    /// результату не має, лишаються ECR_ONLY (раніше клас ставився за наявністю не-Running члена групи).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void ECR_NON_RUNNING_лише_коли_на_не_Running_рядку_є_результат()
    {
        using var f = BuildSingleRowFixture("Removed", twoRows: true);
        File.WriteAllLines(f.Oracle, [TsvLine("E1", 1, "N1", "0.001", "ECW_C05_02")]);

        static object Res(string row, string output, string value) =>
            new { document = Doc, period = 202601, methodology = "ECW_C05_02", rowKey = row, outputCode = output, value };
        var state = new
        {
            methodologies = new object[] { new { code = "ECW_C05_02", published = 1, bound = 1, tables = new[] { "T3_00" } } },
            tables = new object[] { new { document = Doc, period = 202601, table = "T3_00", rowKeys = new[] { "p9-0001", "p9-0002" }, maxRows = 5 } },
            results = new[]
            {
                Res("p9-0001", "ECW_EC_tons_301", "0.001"),
                Res("p9-0001", "ECW_EC_tons_302", "0.5"),   // не-Running рядок p9-0002 результату не має
            },
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));

        var (root, _, _) = RunCompare(f, "nonrunning-empty", "-AggregateBy", "NaePhase");
        var classes = Classes(root);

        Assert.Equal(1, classes["AGG_ECR_ONLY"]);
        Assert.False(classes.ContainsKey("AGG_ECR_NON_RUNNING"));
        Assert.Equal(0, root.GetProperty("ecrNonRunningCells").GetInt32());
    }

    /// <summary>
    /// Сторож P3 (Verify-LandTemplate.ps1): чиста функція знаходить Published методологію без активної прив'язки;
    /// чернетка й методологія з активною прив'язкою не потрапляють; сам Verify її викликає.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Verify_знаходить_опубліковані_методології_без_активної_прив_язки()
    {
        var script = Path.Combine(Path.GetTempPath(), $"ecr-land-inactive-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $info = @(
                [pscustomobject]@{ Code = 'M_OK'; Published = $true; Bindings = @($true, $false) },
                [pscustomobject]@{ Code = 'M_OFF'; Published = $true; Bindings = @($false, $false) },
                [pscustomobject]@{ Code = 'M_NONE'; Published = $true; Bindings = @() },
                [pscustomobject]@{ Code = 'M_DRAFT'; Published = $false; Bindings = @($false) }
            )
            foreach ($r in @(Get-LandInactiveBindingMethodologies $info)) { Write-Host ('INACTIVE=' + $r.Code + ':' + $r.Reason) }
            $mdl = '{"sheets":[{"code":"S","calc":{"methodologies":["A"]},"tables":[{"code":"T","calc":{"methodologies":["B","A"]}}]}]}' | ConvertFrom-Json
            Write-Host ('CODES=' + (@(Get-LandModelMethodologyCodes $mdl) -join ','))
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var (exit, output) = RunPowerShell(script, LandFile("LandTemplateDiff.Lib.ps1"));
            Assert.True(exit == 0, output);
            Assert.Contains("INACTIVE=M_NONE:NO_BINDINGS", output, StringComparison.Ordinal);
            Assert.Contains("INACTIVE=M_OFF:ALL_INACTIVE", output, StringComparison.Ordinal);
            Assert.DoesNotContain("M_OK", output, StringComparison.Ordinal);
            Assert.DoesNotContain("M_DRAFT", output, StringComparison.Ordinal);
            Assert.Contains("CODES=A,B", output, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(script);
        }

        var verify = File.ReadAllText(LandFile("Verify-LandTemplate.ps1"));
        Assert.Contains("Get-LandInactiveBindingMethodologies", verify, StringComparison.Ordinal);
        Assert.Contains("/bindings\"", verify, StringComparison.Ordinal);
    }

    /// <summary>Мутаційний сторож: SQL-вибірка результатів бере TableInstanceId і передає код таблиці в Add-LandParityEcrResult.</summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Вибірка_результатів_бере_TableInstanceId()
    {
        var lib = File.ReadAllText(LandFile("LandParity.Lib.ps1"));
        Assert.Contains("cr.TableInstanceId", lib, StringComparison.Ordinal);
        Assert.Contains("[string]$TableCode = ''", lib, StringComparison.Ordinal);
        Assert.Contains("$targets = @($TableCode.Trim())", lib, StringComparison.Ordinal);
    }
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Місяці_фільтрують_періоди_документів()
    {
        using var f = BuildFixture();
        var (root, _, _) = RunCompare(f, "m2", "-Months", "2", "-GroupBy", "Land_TypeFuel");
        var classes = Classes(root);
        Assert.Equal(12, classes["AMBIGUOUS_KEY"]);
        Assert.False(classes.ContainsKey("MATCH"));
        Assert.False(classes.ContainsKey("ROW_NOT_LOADED_CAPPED"));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Репозиторій_не_містить_прод_вивантажень_повного_паритету()
    {
        var forbiddenFiles = new[]
        {
            "parity-oracle*.tsv", "parity-full*.summary.json", "parity-full*.cells.tsv", "*.local.tsv", "LAND-PROD-*.json",
        };
        var skip = new[] { Path.DirectorySeparatorChar + "node_modules" + Path.DirectorySeparatorChar, Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar };
        foreach (var pattern in forbiddenFiles)
        {
            var hits = Directory.EnumerateFiles(SourceTree.Root, pattern, SearchOption.AllDirectories)
                .Where(f => !skip.Any(s => f.Contains(s, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            Assert.True(hits.Count == 0, $"Файли виду {pattern} не мають бути в репозиторії: {string.Join(", ", hits.Take(3))}");
        }

        foreach (var name in new[] { "Compare-LandParityFull.ps1", "LandParity.Lib.ps1", "Export-LandParityOracleFull.ps1" })
        {
            var text = File.ReadAllText(LandFile(name));
            Assert.DoesNotContain("ecr-backup", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prod-sample", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("pseudonyms", text, StringComparison.OrdinalIgnoreCase);
        }

        // Бібліотека не пише на диск і не виконує записуючих SQL (лише SELECT під READ UNCOMMITTED).
        var lib = StripComments(File.ReadAllText(LandFile("LandParity.Lib.ps1")));
        foreach (var m in Regex.Matches(lib, "\"(?<s>[^\"]*)\"").Cast<Match>())
        {
            Assert.DoesNotMatch(@"(?i)\b(INSERT\s+INTO|UPDATE\s+\S+\s+SET|DELETE\s+FROM|DROP\s|TRUNCATE\s|ALTER\s|EXEC(UTE)?\s|MERGE\s)", m.Groups["s"].Value);
        }

        Assert.DoesNotContain("WriteAllText", lib, StringComparison.Ordinal);
        Assert.Contains("READ UNCOMMITTED", lib, StringComparison.Ordinal);
    }
}
