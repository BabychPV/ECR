using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Ключ рядка Land (LAND-5094, діагноз Б): p&lt;doc&gt;-&lt;TableCode&gt;-&lt;seq&gt; замість p&lt;doc&gt;-&lt;seq&gt;, що повторювався в
/// кожній таблиці документа. Сторожі на СИНТЕТИЧНИХ даних (значень прода немає): (а) генерація і розбір обох форматів,
/// (б) план завантаження будує обидва формати, (в) harness: новий ключ приписує результат лише своїй таблиці,
/// старий ключ із кількома таблицями-кандидатами й різними значеннями дає AMBIGUOUS_ECR_KEY (без значень у виході).
/// </summary>
public sealed class LandRowKeyTests
{
    private static readonly string[] PowerShellPrefix = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"];

    internal static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    internal static (int Exit, string Output) RunPowerShell(params string[] args)
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

    private static JsonElement RunScript(string body)
    {
        var dir = Directory.CreateTempSubdirectory("ecr-rowkey-");
        try
        {
            var script = Path.Combine(dir.FullName, "probe.ps1");
            File.WriteAllText(
                script,
                "$ErrorActionPreference = 'Stop'\n" +
                $". '{LandFile("LandRowKey.Lib.ps1").Replace("'", "''")}'\n" +
                $". '{LandFile("LandInputCells.Lib.ps1").Replace("'", "''")}'\n" +
                body);
            var (exit, output) = RunPowerShell(script);
            Assert.True(exit == 0, output);
            var json = output.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith('{') || l.StartsWith('['));
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ключ_генерується_і_розбирається_в_обох_форматах()
    {
        var root = RunScript("""
            $r = [ordered]@{
              table = New-LandRowKey -DocId 10 -TableCode 'T3_00' -Index 7
              legacy = New-LandRowKey -DocId 10 -Index 7 -KeyFormat Legacy
              pTable = ConvertFrom-LandRowKey 'p10-T11a_01-0012'
              pLegacy = ConvertFrom-LandRowKey 'p10-0012'
              pUnknown = ConvertFrom-LandRowKey 'row-1'
              badTable = $(try { New-LandRowKey -DocId 1 -TableCode 'T3-00' -Index 1; 'no-error' } catch { 'error' })
            }
            $r | ConvertTo-Json -Depth 4 -Compress
            """);

        Assert.Equal("p10-T3_00-0007", root.GetProperty("table").GetString());
        Assert.Equal("p10-0007", root.GetProperty("legacy").GetString());
        var table = root.GetProperty("pTable");
        Assert.Equal("Table", table.GetProperty("Format").GetString());
        Assert.Equal(10, table.GetProperty("Doc").GetInt32());
        Assert.Equal("T11a_01", table.GetProperty("Table").GetString());
        Assert.Equal(12, table.GetProperty("Index").GetInt32());
        var legacy = root.GetProperty("pLegacy");
        Assert.Equal("Legacy", legacy.GetProperty("Format").GetString());
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("Table").ValueKind);
        Assert.Equal(12, legacy.GetProperty("Index").GetInt32());
        Assert.Equal("Unknown", root.GetProperty("pUnknown").GetProperty("Format").GetString());
        Assert.Equal("error", root.GetProperty("badTable").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_завантаження_будує_ключі_обох_форматів()
    {
        var root = RunScript("""
            $doc = @{ periods = @{ '2026-01' = @{ 'AF_Air_Land_Off_Utility_DI_10_03' = @{ rows = @(
                @{ Land_Measure_GTU_Month = 'x' }, @{ Land_Measure_GTU_Month = 'y' } ) } } } }
            $legacy = New-LandInputCellPlan -KindName Gtu -DocumentName 'LAND-PROD-10' -Document $doc
            $table = New-LandInputCellPlan -KindName Gtu -DocumentName 'LAND-PROD-10' -Document $doc -RowKeyFormat Table
            [ordered]@{
              legacy = @($legacy['LAND-PROD-10|202601|T10_03'] | ForEach-Object { $_.RowKey })
              table = @($table['LAND-PROD-10|202601|T10_03'] | ForEach-Object { $_.RowKey })
            } | ConvertTo-Json -Depth 4 -Compress
            """);

        Assert.Equal(["p10-0001", "p10-0002"], root.GetProperty("legacy").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["p10-T10_03-0001", "p10-T10_03-0002"], root.GetProperty("table").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    // ---- harness ----------------------------------------------------------------------------------------------

    private const string Doc = "LAND-PROD-7";
    private const string SentinelA = "0.1112223334";
    private const string SentinelB = "0.2223334445";

    private sealed class Fixture : IDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("ecr-rowkey-harness-");

        public string Oracle => Path.Combine(Dir.FullName, "oracle.local.tsv");

        public string State => Path.Combine(Dir.FullName, "state.json");

        public void Dispose() => Dir.Delete(recursive: true);
    }

    private static Fixture Build(string[] rowKeysT3, string[] rowKeysT4, string[] resultKeys)
    {
        var f = new Fixture();
        var columns = new[] { "Land_No", "Land_NAE", "Land_Status", "Land_TypeFuel" };
        static Dictionary<string, object> Row(string nae) =>
            new() { ["_efid"] = "E1", ["Land_No"] = 1, ["Land_NAE"] = nae, ["Land_Status"] = "Running", ["Land_TypeFuel"] = "Diesel" };
        var sample = new
        {
            doc = Doc,
            periods = new Dictionary<string, object>
            {
                ["2026-01"] = new Dictionary<string, object>
                {
                    ["AF_Air_Land_Stationary_Equipment"] = new { columns, rows = new[] { Row("N1") } },
                    ["AF_Air_Land_Mobile_Equipment"] = new { columns, rows = new[] { Row("N2") } },
                },
            },
        };
        File.WriteAllText(Path.Combine(f.Dir.FullName, Doc + ".json"), JsonSerializer.Serialize(sample));

        static string Line(string table, string nae, string tons) =>
            string.Join('\t', "E1", 1, table, nae, "301", "Diesel", tons, string.Empty, "ECW_C05_02", "2026-01-01T00:00:00", "CalcResult_X");
        File.WriteAllLines(f.Oracle,
        [
            Line("AF_Air_Land_Stationary_Equipment", "N1", SentinelA),
            Line("AF_Air_Land_Mobile_Equipment", "N2", SentinelB),
        ]);

        static object Res(string row, string value) =>
            new { document = Doc, period = 202601, methodology = "ECW_C05_02", rowKey = row, outputCode = "ECW_EC_tons_301", value };
        var state = new
        {
            methodologies = new object[] { new { code = "ECW_C05_02", published = 1, bound = 1, tables = new[] { "T3_00", "T4_01" } } },
            tables = new object[]
            {
                new { document = Doc, period = 202601, table = "T3_00", rowKeys = rowKeysT3, maxRows = 50 },
                new { document = Doc, period = 202601, table = "T4_01", rowKeys = rowKeysT4, maxRows = 50 },
            },
            results = new[] { Res(resultKeys[0], SentinelA), Res(resultKeys[1], SentinelB) },
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));
        return f;
    }

    private static (JsonElement Root, string Summary, string Cells) Compare(Fixture f)
    {
        var prefix = Path.Combine(f.Dir.FullName, "out");
        var (exit, output) = RunPowerShell(
            LandFile("Compare-LandParityFull.ps1"), "-JsonDir", f.Dir.FullName, "-OracleTsv", f.Oracle, "-StateFile", f.State, "-OutPrefix", prefix);
        Assert.True(exit == 0, output);
        var summary = File.ReadAllText(prefix + ".summary.json");
        return (JsonDocument.Parse(summary).RootElement.Clone(), summary, File.ReadAllText(prefix + ".cells.tsv"));
    }

    private static Dictionary<string, int> Classes(JsonElement root) =>
        root.GetProperty("classes").EnumerateArray()
            .ToDictionary(e => e.GetProperty("class").GetString()!, e => e.GetProperty("cells").GetInt32());

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Ключ_з_кодом_таблиці_приписується_лише_своїй_таблиці_без_перезапису()
    {
        using var f = Build(
            ["p7-T3_00-0001"], ["p7-T4_01-0001"], ["p7-T3_00-0001", "p7-T4_01-0001"]);
        var (root, summary, cells) = Compare(f);
        var classes = Classes(root);

        Assert.Equal(2, classes["MATCH"]);
        Assert.False(classes.ContainsKey("AMBIGUOUS_ECR_KEY"));
        Assert.Equal(0, root.GetProperty("ambiguousEcrKeys").GetProperty("keys").GetInt32());
        Assert.Equal(0, root.GetProperty("multiTableResults").GetArrayLength());
        var formats = root.GetProperty("rowKeyFormats");
        Assert.Equal(2, formats.GetProperty("table").GetInt32());
        Assert.Equal(0, formats.GetProperty("legacy").GetInt32());
        Assert.Equal(6, root.GetProperty("formatVersion").GetInt32());
        Assert.DoesNotContain(SentinelA, summary + cells, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelB, summary + cells, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Старий_ключ_у_кількох_таблицях_із_різними_значеннями_дає_AMBIGUOUS_ECR_KEY()
    {
        using var f = Build(["p7-0001"], ["p7-0001"], ["p7-0001", "p7-0001"]);
        var (root, summary, cells) = Compare(f);
        var classes = Classes(root);

        Assert.Equal(2, classes["AMBIGUOUS_ECR_KEY"]);   // а не «останній виграє»: MATCH + MISMATCH
        Assert.False(classes.ContainsKey("MATCH"));
        Assert.DoesNotContain(classes.Keys, c => c.StartsWith("MISMATCH", StringComparison.Ordinal));
        var amb = root.GetProperty("ambiguousEcrKeys");
        Assert.True(amb.GetProperty("keys").GetInt32() >= 1);
        Assert.Equal("ECW_C05_02", amb.GetProperty("byMethodology")[0].GetProperty("methodology").GetString());
        Assert.Equal(2, root.GetProperty("rowKeyFormats").GetProperty("legacy").GetInt32());
        Assert.DoesNotContain(SentinelA, summary + cells, StringComparison.Ordinal);
        Assert.DoesNotContain(SentinelB, summary + cells, StringComparison.Ordinal);

        // Клас лишається формату 3: у bucket «other», значення не порівнювалось.
        Assert.Contains("AMBIGUOUS_ECR_KEY", cells, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Старий_ключ_з_однією_завантаженою_таблицею_лишається_порівнюваним()
    {
        // Рядок є лише в T3_00: кандидат один, колізії немає - сумісність зі старим форматом.
        using var f = Build(["p7-0001"], [], ["p7-0001", "p7-0002"]);
        var (root, _, _) = Compare(f);
        var classes = Classes(root);

        Assert.Equal(1, classes["MATCH"]);
        Assert.False(classes.ContainsKey("AMBIGUOUS_ECR_KEY"));
    }
}
