using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Паритет C12_02 / C12_03 у harness повного набору (tools/land/LandParity.Lib.ps1, Compare-LandParityFull.ps1):
/// вихід ECR `ECW_EC_tons_XXX` + речовина і `ECW_EC_tons_&lt;код&gt;_type1/_type2` зіставляється з ключем старої системи
/// `ECW_EC_tons_&lt;код&gt;` (раніше harness дав MISSING_OUTPUT/ECR_ONLY на всьому, репетиція вікна 13.10, B4).
/// Дані - СИНТЕТИЧНІ, прода тут немає; звіт без значень.
/// </summary>
public sealed class LandParityC12CompareTests
{
    private static readonly string[] PowerShellPrefix = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File"];

    private static string LandDir => Path.Combine(SourceTree.Root, "tools", "land");

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

    // ---- чиста логіка: ім'я виходу -> ключ старої системи, групи звіту ----

    private const string LogicScript = """
        param([string]$LibDir)
        $ErrorActionPreference = 'Stop'
        . (Join-Path $LibDir 'LandParity.Lib.ps1')
        . (Join-Path $LibDir 'LandParityBranch.Lib.ps1')
        $st = New-LandParityState
        $canon = foreach ($c in @(
            @('ECW_C12_02', 'ECW_EC_tons_301_type1', ''),
            @('ECW_C12_02', 'ECW_EC_gsec_415_type2', ''),
            @('ECW_C12_02', 'ECW_EC_tons_415_type1', ''),
            @('ECW_C12_03', 'ECW_EC_tons_XXX', '416'),
            @('ECW_C12_03', 'ECW_EC_tons_XXX', ''),
            @('ECW_C12_09', 'ECW_EC_tons_301_type1', ''),
            @('ECW_C12_02', 'ECW_EC_tons_301_type1', '304'),
            @('ECW_C05_02', 'ECW_EC_tons_301', ''),
            @('ECW_C12_02', 'ECW_EC_tons_NOx', ''),
            @('ECW_C12_02', 'ECW_EC_tons_301_type2', '301'))) {
            $r = ConvertTo-LandParityCanonicalOutput $st $c[0] $c[1] $c[2]
            [ordered]@{ code = $r.Code; variant = $r.Variant; ignore = [bool]$r.Ignore }
        }
        $buckets = foreach ($b in @(
            @('MATCH', 0.0), @('NEAR', 5e-10), @('NEAR', 5e-9), @('MISMATCH_ECR_ZERO', 1.0), @('AGG_ECR_ONLY_ZERO', 0.0),
            @('MISSING_OUTPUT_ZERO', 0.0), @('LEGACY_STATUS_SKIP', 0.0), @('AMBIGUOUS_KEY', 0.0), @('BRANCH_BOTH_PRESENT', 0.0),
            @('INCOMPLETE_ROW_NOT_LOADED_CAPPED', 0.0), @('MISMATCH', 5e-10))) {
            $r = Get-LandParityBucket $b[0] ([double]$b[1]) 1e-9
            [ordered]@{ group = $r.Group; within = [bool]$r.Within; zero = [bool]$r.Zero }
        }
        $branch = [ordered]@{
            fold = @($st.TypeFold)
            nullType1 = @{ }
        }
        $branch.nullType1 = [ordered]@{}
        foreach ($k in $st.NullType1.Keys) { $branch.nullType1[$k] = @($st.NullType1[$k]) }
        $branch.branchMethodologies = @((Get-LandParityBranchMethodologies).Keys)
        $branch.branchNullType1 = [ordered]@{}
        $nt = Get-LandParityBranchNullType1Methodologies
        foreach ($k in $nt.Keys) { $branch.branchNullType1[$k] = @($nt[$k]) }
        [ordered]@{ canon = @($canon); buckets = @($buckets); branch = $branch } | ConvertTo-Json -Depth 6 -Compress
        """;

    private static JsonElement RunLogic()
    {
        var dir = Directory.CreateTempSubdirectory("ecr-c12-logic-");
        try
        {
            var script = Path.Combine(dir.FullName, "logic.ps1");
            File.WriteAllText(script, LogicScript, new UTF8Encoding(false));
            var (exit, output) = RunPowerShell(script, "-LibDir", LandDir);
            Assert.True(exit == 0, output);
            var json = output.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith('{'));
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Імена_виходів_ECR_зводяться_до_ключа_старої_системи()
    {
        var canon = RunLogic().GetProperty("canon").EnumerateArray()
            .Select(e => (Code: e.GetProperty("code").GetString()!, Variant: e.GetProperty("variant").GetString()!, Ignore: e.GetProperty("ignore").GetBoolean()))
            .ToArray();

        Assert.Equal(("ECW_EC_tons_301", "type1", false), canon[0]);          // C12_02: гілку знято
        Assert.Equal(("ECW_EC_gsec_415", "type2", false), canon[1]);
        Assert.Equal(("ECW_EC_tons_415_type1", string.Empty, false), canon[2]); // type1 коду 415 у старому коді немає -> лишається ECR_ONLY
        Assert.Equal(("ECW_EC_tons_416", string.Empty, false), canon[3]);     // C12_03: XXX + речовина
        Assert.Equal(("ECW_EC_tons_XXX", string.Empty, false), canon[4]);     // без речовини ім'я не вигадується
        Assert.Equal(("ECW_EC_tons_301_type1", string.Empty, false), canon[5]); // інша методологія: гілку не знімаємо
        Assert.True(canon[6].Ignore);                                         // код 301 у імені, прохід речовини 304
        Assert.Equal(("ECW_EC_tons_301", string.Empty, false), canon[7]);     // звичайний ключ без змін
        Assert.Equal(("ECW_EC_tons_NOx", string.Empty, false), canon[8]);     // проміжний вихід без змін
        Assert.Equal(("ECW_EC_tons_301", "type2", false), canon[9]);          // речовина = код у імені
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Групи_звіту_і_поріг_відносної_похибки()
    {
        var buckets = RunLogic().GetProperty("buckets").EnumerateArray()
            .Select(e => (Group: e.GetProperty("group").GetString()!, Within: e.GetProperty("within").GetBoolean(), Zero: e.GetProperty("zero").GetBoolean()))
            .ToArray();

        Assert.Equal(("MATCHED", false, false), buckets[0]);
        Assert.Equal(("MATCHED", true, false), buckets[1]);      // NEAR у межах порога
        Assert.Equal(("MISMATCH", false, false), buckets[2]);    // NEAR поза порогом
        Assert.Equal(("MISMATCH", false, false), buckets[3]);
        Assert.Equal(("ECR_ONLY", false, true), buckets[4]);
        Assert.Equal(("ORACLE_ONLY", false, true), buckets[5]);
        Assert.Equal(("EXPLAINED_SKIP", false, false), buckets[6]);
        Assert.Equal("OTHER", buckets[7].Group);
        Assert.Equal("OTHER", buckets[8].Group);
        Assert.Equal("OTHER", buckets[9].Group);
        Assert.Equal(("MATCHED", true, false), buckets[10]);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Налаштування_гілок_harness_збігаються_з_етапом_ParityBranch()
    {
        var branch = RunLogic().GetProperty("branch");
        var fold = branch.GetProperty("fold").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var methodologies = branch.GetProperty("branchMethodologies").EnumerateArray().Select(e => e.GetString()!).ToArray();

        // Кожна методологія з етапу ParityBranch знімається harness-ом за замовчуванням (і навпаки).
        Assert.Equal(methodologies.Select(m => $"^{m}$").OrderBy(x => x, StringComparer.Ordinal), fold.OrderBy(x => x, StringComparer.Ordinal));

        var expected = branch.GetProperty("branchNullType1").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetString()!).ToArray());
        var actual = branch.GetProperty("nullType1").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(expected.Keys.OrderBy(x => x), actual.Keys.OrderBy(x => x));
        foreach (var (name, codes) in expected)
        {
            Assert.Equal(codes, actual[name]);
        }
    }

    // ---- повний прогін на синтетичній фікстурі ----

    private const string Doc = "LAND-PROD-12";
    private const string TableGtu = "AF_Air_Land_Off_Utility_DI_10_03";   // T10_03: C12_02
    private const string TableCold = "AF_Air_Land_Off_Utility_DI_10_01";  // T10_01: C12_03

    // Значення «прода» (синтетичні сентинели), яких у виході бути не має.
    private const string SentinelGtuMismatch = "2.7182818";
    private const string SentinelColdMismatch = "0.3141592";
    private const string SentinelBoth = "0.2468013";
    private static readonly string[] Sentinels = [SentinelGtuMismatch, SentinelColdMismatch, SentinelBoth, "0.5000000001"];

    private static Dictionary<string, object> Row(int no, string nae) =>
        new() { ["_efid"] = "E1", ["Land_No"] = no, ["Land_NAE"] = nae, ["Land_Status"] = "Running", ["Land_TypeFuel"] = "FG_Makat" };

    private static string Legacy(string table, string nae, string component, string tons, string methodology) =>
        string.Join('\t', "E1", 1, table, nae, component, "FG_Makat", tons, string.Empty, methodology, "2026-01-01T00:00:00", "CalcResult_Emissions_Air_Land_X");

    private static object Res(string meth, string row, string output, string value, string? substance = null) =>
        substance is null
            ? new { document = Doc, period = 202601, methodology = meth, rowKey = row, outputCode = output, value }
            : new { document = Doc, period = 202601, methodology = meth, rowKey = row, outputCode = output, value, substanceCode = substance };

    private sealed class Fixture : IDisposable
    {
        public DirectoryInfo Dir { get; } = Directory.CreateTempSubdirectory("ecr-parity-c12-");

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
                    [TableGtu] = new { columns, rows = new[] { Row(1, "0035"), Row(2, "0037"), Row(3, "0039"), Row(4, "0041") } },
                    [TableCold] = new { columns, rows = new[] { Row(1, "0026") } },
                },
            },
        };
        File.WriteAllText(Path.Combine(f.Dir.FullName, Doc + ".json"), JsonSerializer.Serialize(sample));

        File.WriteAllLines(f.Oracle,
        [
            // C12_02: гілку старий код пише одну, ключ = код
            Legacy(TableGtu, "0035", "301", "0.125", "ECW_C12_02"),
            Legacy(TableGtu, "0035", "415", "0.375", "ECW_C12_02"),
            Legacy(TableGtu, "0037", "304", "0.25", "ECW_C12_02"),
            Legacy(TableGtu, "0039", "330", "0.5", "ECW_C12_02"),
            Legacy(TableGtu, "0041", "337", "2", "ECW_C12_02"),
            // C12_03: ключ = код речовини
            Legacy(TableCold, "0026", "333", "0.75", "ECW_C12_03"),
            Legacy(TableCold, "0026", "416", "0.6", "ECW_C12_03"),
            Legacy(TableCold, "0026", "2754", "0.2", "ECW_C12_03"),
        ]);

        var state = new
        {
            methodologies = new object[]
            {
                new { code = "ECW_C12_02", published = 1, bound = 1, tables = Array.Empty<string>() },
                new { code = "ECW_C12_03", published = 1, bound = 1, tables = Array.Empty<string>() },
            },
            tables = new object[]
            {
                new { document = Doc, period = 202601, table = "T10_03", rowKeys = new[] { "p12-0001", "p12-0002", "p12-0003", "p12-0004" }, maxRows = 100 },
                new { document = Doc, period = 202601, table = "T10_01", rowKeys = new[] { "p12-0001" }, maxRows = 100 },
            },
            results = new[]
            {
                Res("ECW_C12_02", "p12-0001", "ECW_EC_tons_301_type1", "0.125"),
                Res("ECW_C12_02", "p12-0001", "ECW_EC_tons_415_type2", "0.375"),
                Res("ECW_C12_02", "p12-0002", "ECW_EC_tons_304_type1", SentinelBoth),
                Res("ECW_C12_02", "p12-0002", "ECW_EC_tons_304_type2", SentinelBoth),
                Res("ECW_C12_02", "p12-0002", "ECW_EC_tons_415_type1", "0.5"),
                Res("ECW_C12_02", "p12-0003", "ECW_EC_tons_330_type2", "0.5000000001"),
                Res("ECW_C12_02", "p12-0004", "ECW_EC_tons_337_type2", SentinelGtuMismatch),
                Res("ECW_C12_03", "p12-0001", "ECW_EC_tons_XXX", "0.75", "333"),
                Res("ECW_C12_03", "p12-0001", "ECW_EC_tons_XXX", SentinelColdMismatch, "416"),
                Res("ECW_C12_03", "p12-0001", "ECW_EC_tons_XXX", "0.1", "602"),
                Res("ECW_C12_03", "p12-0001", "ECW_EC_tons_XXX", "0.9"),
            },
        };
        File.WriteAllText(f.State, JsonSerializer.Serialize(state));
        return f;
    }

    private static (JsonElement Root, string Summary, string Cells, string Buckets) RunCompare(Fixture f, string name, params string[] extra)
    {
        var prefix = f.Prefix(name);
        var args = new List<string>
        {
            Path.Combine(LandDir, "Compare-LandParityFull.ps1"), "-JsonDir", f.Dir.FullName, "-OracleTsv", f.Oracle, "-StateFile", f.State,
            "-OutPrefix", prefix,
        };
        args.AddRange(extra);
        var (exit, output) = RunPowerShell([.. args]);
        Assert.True(exit == 0, output);
        var summary = File.ReadAllText(prefix + ".summary.json");
        return (JsonDocument.Parse(summary).RootElement.Clone(), summary, File.ReadAllText(prefix + ".cells.tsv"), File.ReadAllText(prefix + ".buckets.tsv"));
    }

    private static Dictionary<string, int> Counts(JsonElement e, params string[] names) =>
        names.ToDictionary(n => n, n => e.GetProperty(n).GetInt32());

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void C12_02_і_C12_03_зіставляються_з_oracle_по_коду_і_речовині()
    {
        using var f = BuildFixture();
        var (root, summary, cells, bucketsTsv) = RunCompare(f, "c12");

        var byMeth = root.GetProperty("buckets").EnumerateArray().ToDictionary(e => e.GetProperty("methodology").GetString()!, e => e);
        var names = new[] { "matched", "withinThreshold", "mismatch", "ecrOnly", "oracleOnly", "other" };

        // C12_02: 301 і 415 (type2) збіг, 330 - збіг лише за порогом, 337 - розбіжність, 304 - обидві гілки, 415 type1 - лише ECR.
        var c12 = Counts(byMeth["ECW_C12_02"], names);
        Assert.Equal(3, c12["matched"]);
        Assert.Equal(1, c12["withinThreshold"]);
        Assert.Equal(1, c12["mismatch"]);
        Assert.Equal(1, c12["ecrOnly"]);
        Assert.Equal(0, c12["oracleOnly"]);
        Assert.Equal(1, c12["other"]);

        // C12_03: 333 збіг, 416 розбіжність, 2754 немає в ECR, 602 і результат без речовини - лише ECR.
        var cold = Counts(byMeth["ECW_C12_03"], names);
        Assert.Equal(1, cold["matched"]);
        Assert.Equal(1, cold["mismatch"]);
        Assert.Equal(1, cold["oracleOnly"]);
        Assert.Equal(2, cold["ecrOnly"]);
        Assert.Equal(0, cold["other"]);

        var bySub = root.GetProperty("bySubstance").EnumerateArray()
            .ToDictionary(e => e.GetProperty("methodology").GetString() + "/" + e.GetProperty("substance").GetString(), e => e);
        Assert.Equal(1, bySub["ECW_C12_03/416"].GetProperty("mismatch").GetInt32());
        Assert.Equal(1, bySub["ECW_C12_03/2754"].GetProperty("oracleOnly").GetInt32());
        Assert.Equal(1, bySub["ECW_C12_03/602"].GetProperty("ecrOnly").GetInt32());
        Assert.Equal(1, bySub["ECW_C12_03/-"].GetProperty("ecrOnly").GetInt32());           // XXX без речовини
        Assert.Equal(1, bySub["ECW_C12_02/330"].GetProperty("withinThreshold").GetInt32());
        Assert.Equal(1, bySub["ECW_C12_02/304"].GetProperty("other").GetInt32());

        // Результат XXX без речовини названий у звіті.
        var noSub = root.GetProperty("outputsWithoutSubstance").EnumerateArray().Single();
        Assert.Equal("ECW_C12_03", noSub.GetProperty("methodology").GetString());
        Assert.Equal(1, noSub.GetProperty("results").GetInt32());

        // Відносна похибка розбіжності - число, не значення.
        Assert.True(bySub["ECW_C12_02/337"].GetProperty("relMax").GetDouble() > 0.05);
        Assert.Contains("BRANCH_BOTH_PRESENT", cells, StringComparison.Ordinal);
        Assert.StartsWith("methodology\tsubstance\tmatched\twithinThreshold\tmismatch\tecrOnly\toracleOnly\texplainedSkip\tother\tzero\trelP95\trelMax", bucketsTsv, StringComparison.Ordinal);

        // Значень (ні старої системи, ні ECR) у виході немає.
        foreach (var text in new[] { summary, cells, bucketsTsv })
        {
            foreach (var sentinel in Sentinels)
            {
                Assert.DoesNotContain(sentinel, text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Поріг_відносної_похибки_параметр_а_гілки_інших_методологій_додаються()
    {
        using var f = BuildFixture();

        // Поріг 1e-12: 330 (відносно 2e-10) уже розбіжність, а не збіг за порогом.
        var (strict, _, _, _) = RunCompare(f, "strict", "-RelThreshold", "1e-12");
        var c12 = strict.GetProperty("buckets").EnumerateArray().Single(e => e.GetProperty("methodology").GetString() == "ECW_C12_02");
        Assert.Equal(2, c12.GetProperty("matched").GetInt32());
        Assert.Equal(0, c12.GetProperty("withinThreshold").GetInt32());
        Assert.Equal(2, c12.GetProperty("mismatch").GetInt32());

        // Без -TypeFoldMethodology інша методологія гілку не знімає: у ECR лишаються імена з _type1.
        var (def, _, _, _) = RunCompare(f, "def", "-TypeFoldMethodology", "^ECW_C99_01$");
        Assert.Equal(3, def.GetProperty("buckets").EnumerateArray().Single(e => e.GetProperty("methodology").GetString() == "ECW_C12_02").GetProperty("matched").GetInt32());

        var (exit, output) = RunPowerShell(Path.Combine(LandDir, "Compare-LandParityFull.ps1"), "-ValidateOnly", "-TypeFoldMethodology", "([bad");
        Assert.NotEqual(0, exit);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }
}
