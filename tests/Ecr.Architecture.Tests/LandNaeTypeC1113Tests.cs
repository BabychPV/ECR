using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// D-435 (C11_13): вихід `ECW_EC_tons_k_type` / `ECW_EC_gsec_k` пишеться лише для NAE, що одночасно в EC[k] і в
/// catTons[type] (легасі ECW_C11_13.cs:66-133). Перелік виходів - у <c>LandNaeType.Lib.ps1</c>, етап <c>-Stage NaeType</c>
/// обгортає ним формули. Значень прода тут немає; зразок легасі вбудовано дослівно (docs/source не в репозиторії).
/// </summary>
/// <remarks>
/// Мутації (лише цей клас): викинути <c>0523(003)</c> з catTons.type1 у Lib - <see cref="Переліки_виходів_дорівнюють_легасі"/>
/// червоніє; прибрати гілку NULL-виразу в <c>Convert-LandNaeOutputFormula</c> -
/// <see cref="Порожній_перелік_дає_NULL_вираз_gsec_без_type_за_EC"/>; підставити <c>ConvertTo-LandNaeNormalized</c>
/// (відрізання хвоста) - <see cref="Нормалізація_C11_13_не_відрізає_хвіст_003_обидва_написання_в_in_і_C11_10_без_змін"/>;
/// змінити елемент в одному зі списків харнесу - <see cref="Списки_збігаються_зі_списками_харнесу_паритету"/>.
/// </remarks>
public sealed class LandNaeTypeC1113Tests
{
    // ECW_C11_13.cs:76-96, дослівно (рядки EC.Add / catTons.Add).
    private const string LegacyExcerpt = """
        EC.Add("333", new List<string>{ "6670", "6672", "6674" });
        EC.Add("1078", new List<string>{ "6568", "6574", "6575", "6567", "6669", "6671", "6673" });
        EC.Add("2754", new List<string>{ "6670", "6672", "6674" });
        EC.Add("2735", new List<string> { "0500(003)", "0501(003)", "0502(003)", "0503(003)", "0520(003)", "0521(003)", "0522(003)", "0523(003)" });

        catTons.Add("type1", new List<string> { "6567", "6568", "6574", "6575", "0500(003)", "0501(003)", "0502(003)", "0503(003)", "0520(003)", "0521(003)", "0522(003)", "0523(003)" });
        catTons.Add("type2", new List<string> { "6669", "6671", "6673", "6670", "6672", "6674" });
        """;

    private static string Run(string body, params string[] libs)
    {
        var dir = Directory.CreateTempSubdirectory("ecr-c1113-");
        try
        {
            var script = Path.Combine(dir.FullName, "probe.ps1");
            var sb = new StringBuilder("$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n");
            foreach (var lib in libs)
            {
                sb.Append(". '").Append(LandRowKeyTests.LandFile(lib).Replace("'", "''")).Append("'\n");
            }

            sb.Append(body);
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var (exit, output) = LandRowKeyTests.RunPowerShell(script);
            Assert.True(exit == 0, output);
            return output.Split('\n').Select(l => l.Trim()).Last(l => l.StartsWith('{') || l.StartsWith('['));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static Dictionary<string, string[]> Parse(string kind)
    {
        var d = new Dictionary<string, string[]>();
        foreach (Match m in Regex.Matches(LegacyExcerpt, kind + @"\.Add\(""([^""]+)"",\s*new List<string>\s*\{([^}]*)\}"))
        {
            d[m.Groups[1].Value] = Regex.Matches(m.Groups[2].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToArray();
        }

        return d;
    }

    private static string[] Arr(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Переліки_виходів_дорівнюють_легасі()
    {
        var ec = Parse("EC");
        var cat = Parse("catTons");
        Assert.Equal(4, ec.Count);
        Assert.Equal(2, cat.Count);

        var json = Run(
            "(Get-LandNaeOutputGroups)['ECW_C11_13'] | ConvertTo-Json -Compress -Depth 5",
            "LandNaeType.Lib.ps1");
        using var doc = JsonDocument.Parse(json);
        var g = doc.RootElement;

        var expectedCodes = new List<string>();
        foreach (var (k, naes) in ec)
        {
            foreach (var (type, list) in cat)
            {
                var code = $"ECW_EC_tons_{k}_{type}";
                expectedCodes.Add(code);
                Assert.Equal(naes.Where(n => list.Contains(n)).ToArray(), Arr(g.GetProperty(code)));
            }

            // gsec без суфікса type: легасі пише його, коли NAE в EC[k] (і в одному з catTons)
            var gsec = $"ECW_EC_gsec_{k}";
            expectedCodes.Add(gsec);
            Assert.Equal(naes.Where(n => cat.Values.Any(v => v.Contains(n))).ToArray(), Arr(g.GetProperty(gsec)));
            Assert.Equal(naes, Arr(g.GetProperty(gsec)));
        }

        Assert.Equal(expectedCodes.Order().ToArray(), g.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Порожній_перелік_дає_NULL_вираз_gsec_без_type_за_EC()
    {
        var json = Run(
            """
            $g = (Get-LandNaeOutputGroups)['ECW_C11_13']
            $args1 = '@Q;@Land_Measure_OperatingTime?;'
            $n = @{}
            foreach ($c in @('ECW_EC_tons_333_type1', 'ECW_EC_tons_2754_type1', 'ECW_EC_tons_2735_type2')) {
              $r = Convert-LandNaeOutputFormula -Expression 'Q*OperatingTime' -ArgumentsCsv $args1 -Naes @($g[$c])
              $n[$c] = [ordered]@{ n = @($g[$c]).Count; e = $r.Expression; a = $r.ArgumentsCsv; ch = $r.Changed }
            }
            $again = Convert-LandNaeOutputFormula -Expression 'NULL' -ArgumentsCsv $args1 -Naes @()
            $w = Convert-LandNaeOutputFormula -Expression 'Q*OperatingTime' -ArgumentsCsv $args1 -Naes @($g['ECW_EC_tons_333_type2'])
            $gs = Convert-LandNaeOutputFormula -Expression 'if(OperatingTime=0,0,Q)' -ArgumentsCsv $args1 -Naes @($g['ECW_EC_gsec_2735'])
            [ordered]@{ nulls = $n; again = $again.Changed; w = $w.Expression; wa = $w.ArgumentsCsv; gs = $gs.Expression
                        gsec1078 = @($g['ECW_EC_gsec_1078']).Count; gsec333 = @($g['ECW_EC_gsec_333']) } | ConvertTo-Json -Compress -Depth 6
            """,
            "LandNaeType.Lib.ps1");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        foreach (var code in new[] { "ECW_EC_tons_333_type1", "ECW_EC_tons_2754_type1", "ECW_EC_tons_2735_type2" })
        {
            var n = r.GetProperty("nulls").GetProperty(code);
            Assert.Equal(0, n.GetProperty("n").GetInt32());
            Assert.Equal("NULL", n.GetProperty("e").GetString());
            Assert.Equal("@Q;@Land_Measure_OperatingTime?;", n.GetProperty("a").GetString());   // аргументи NULL-виразу не чіпаються
            Assert.True(n.GetProperty("ch").GetBoolean());
        }

        Assert.False(r.GetProperty("again").GetBoolean());
        Assert.Equal(
            "if(in(@Land_NAE, '6670', '6672', '6674'), Q*OperatingTime, NULL)",
            r.GetProperty("w").GetString());
        Assert.Equal("@Q;@Land_Measure_OperatingTime?;@Land_NAE;", r.GetProperty("wa").GetString());
        Assert.StartsWith("if(in(@Land_NAE, '0500(003)', '0500 (003)', '0501(003)', '0501 (003)'", r.GetProperty("gs").GetString(), StringComparison.Ordinal);
        Assert.Equal(7, r.GetProperty("gsec1078").GetInt32());
        Assert.Equal(["6670", "6672", "6674"], Arr(r.GetProperty("gsec333")));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Нормалізація_C11_13_не_відрізає_хвіст_003_обидва_написання_в_in_і_C11_10_без_змін()
    {
        var json = Run(
            """
            $g = (Get-LandNaeOutputGroups)['ECW_C11_13']
            $r = Convert-LandNaeOutputFormula -Expression 'X' -ArgumentsCsv '@X;' -Naes @($g['ECW_EC_tons_2735_type1'])
            $c10 = Get-LandNaeTypeGroups
            $t2 = @($c10['ECW_C11_10']['type2'])
            $c10r = Convert-LandNaeTypeFormula -Expression '1' -ArgumentsCsv '@X;' -Naes $t2
            [ordered]@{
              n13 = ConvertTo-LandNaeC1113Normalized ' 0500 (003) '
              n10 = ConvertTo-LandNaeNormalized ' 0500 (003) '
              expr = $r.Expression
              keys = @($c10.Keys)
              c10head = $c10r.Expression
              c10 = Test-LandNaeInType '0647 (001-002)' 'ECW_C11_10' 'type2'
              c13in = Test-LandNaeInType '0500(003)' 'ECW_C11_13' 'type1'
              ty = @((Get-LandNaeTypeOfFormula 'ECW_EC_gsec_1078'), (Get-LandNaeTypeOfFormula 'ECW_EC_tons_1078_type1'))
            } | ConvertTo-Json -Compress -Depth 5
            """,
            "LandNaeType.Lib.ps1");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        Assert.Equal("0500(003)", r.GetProperty("n13").GetString());   // хвіст лишається частиною ключа
        Assert.Equal("0500", r.GetProperty("n10").GetString());        // C11_10: хвіст відрізається - для C11_13 непридатна
        var e = r.GetProperty("expr").GetString()!;
        foreach (var d in new[] { "0500", "0501", "0502", "0503", "0520", "0521", "0522", "0523" })
        {
            Assert.Contains($"'{d}(003)', '{d} (003)'", e, StringComparison.Ordinal);
        }

        // C11_10 без змін: словник, обгортка, регулярний вираз типу; C11_13 у словнику типів немає
        Assert.Equal(["ECW_C11_10"], r.GetProperty("keys").EnumerateArray().Select(x => x.GetString()!).ToArray());
        Assert.StartsWith("if(in(@Land_NAE, '0647', '0647 (001-002)', '0648', '0648 (001-002)'", r.GetProperty("c10head").GetString() + string.Empty, StringComparison.Ordinal);
        Assert.True(r.GetProperty("c10").GetBoolean());
        Assert.False(r.GetProperty("c13in").GetBoolean());
        var ty = r.GetProperty("ty").EnumerateArray().ToArray();
        Assert.Equal(JsonValueKind.Null, ty[0].ValueKind);             // gsec без суфікса type - регулярний вираз не спрацьовує
        Assert.Equal("type1", ty[1].GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Обгортка_виходів_ідемпотентна_повтор_нуль_змін()
    {
        var json = Run(
            """
            $g = (Get-LandNaeOutputGroups)['ECW_C11_13']
            $naes = @($g['ECW_EC_tons_1078_type1'])
            $r1 = Convert-LandNaeOutputFormula -Expression 'Q*n' -ArgumentsCsv '@Q;@n;' -Naes $naes
            $r2 = Convert-LandNaeOutputFormula -Expression $r1.Expression -ArgumentsCsv $r1.ArgumentsCsv -Naes $naes
            $z1 = Convert-LandNaeOutputFormula -Expression 'Q*n' -ArgumentsCsv '@Q;@n;' -Naes @()
            $z2 = Convert-LandNaeOutputFormula -Expression $z1.Expression -ArgumentsCsv $z1.ArgumentsCsv -Naes @()
            [ordered]@{ e1 = $r1.Expression; a1 = $r1.ArgumentsCsv; c2 = $r2.Changed; e2 = $r2.Expression; a2 = $r2.ArgumentsCsv
                        z2 = $z2.Changed; ze = $z2.Expression } | ConvertTo-Json -Compress
            """,
            "LandNaeType.Lib.ps1");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        Assert.Equal("if(in(@Land_NAE, '6568', '6574', '6575', '6567'), Q*n, NULL)", r.GetProperty("e1").GetString());
        Assert.Equal("@Q;@n;@Land_NAE;", r.GetProperty("a1").GetString());
        Assert.False(r.GetProperty("c2").GetBoolean());
        Assert.Equal(r.GetProperty("e1").GetString(), r.GetProperty("e2").GetString());
        Assert.Equal("@Q;@n;@Land_NAE;", r.GetProperty("a2").GetString());   // @Land_NAE додано один раз
        Assert.False(r.GetProperty("z2").GetBoolean());
        Assert.Equal("NULL", r.GetProperty("ze").GetString());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_NaeType_бере_виходи_C11_13_перед_OptionalArguments_і_рядок_рішення_D435()
    {
        var build = File.ReadAllText(LandRowKeyTests.LandFile("Build-LandTemplate.ps1"));
        var start = build.IndexOf("function Stage-NaeType", StringComparison.Ordinal);
        var end = build.IndexOf("\nfunction ", start + 10, StringComparison.Ordinal);
        var stage = build[start..end];
        Assert.Contains("Get-LandNaeOutputGroups", stage, StringComparison.Ordinal);
        Assert.Contains("Convert-LandNaeOutputFormula", stage, StringComparison.Ordinal);
        Assert.Contains("опублікована - не чіпаю", stage, StringComparison.Ordinal);
        Assert.True(
            build.IndexOf("Stage-NaeType }", StringComparison.Ordinal) < build.IndexOf("Stage-OptionalArguments }", StringComparison.Ordinal));
        Assert.Matches(@"if \(\$Stage -contains 'NaeType'\) \{ Stage-NaeType \}", build);

        var readme = File.ReadAllText(LandRowKeyTests.LandFile("README.md"));
        Assert.Contains("D-435", readme, StringComparison.Ordinal);

        var decisions = File.ReadAllText(Path.Combine(SourceTree.Root, "docs", "tz", "10-decisions.md"));
        Assert.Matches(@"\|\s*\*\*D-435\*\*", decisions);
        Assert.Matches(@"\| 2026-10-15 \| `D-435`", decisions);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Списки_збігаються_зі_списками_харнесу_паритету()
    {
        var json = Run(
            """
            [ordered]@{
              t1 = @($script:LandNaeC1113CatTons['type1']); t2 = @($script:LandNaeC1113CatTons['type2'])
              h1 = @($script:LandParityC1113Type1); h2 = @($script:LandParityC1113Type2)
            } | ConvertTo-Json -Compress
            """,
            "LandNaeType.Lib.ps1",
            "LandParity.Lib.ps1");
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        Assert.Equal(12, Arr(r.GetProperty("t1")).Length);
        Assert.Equal(6, Arr(r.GetProperty("t2")).Length);
        Assert.Equal(Arr(r.GetProperty("h1")), Arr(r.GetProperty("t1")));
        Assert.Equal(Arr(r.GetProperty("h2")), Arr(r.GetProperty("t2")));

        var cat = Parse("catTons");
        Assert.Equal(cat["type1"], Arr(r.GetProperty("h1")));
        Assert.Equal(cat["type2"], Arr(r.GetProperty("h2")));
    }
}
