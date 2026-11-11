using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// AN-171/AN-172: one `_typeN` branch per (row, substance, metric), as the legacy code writes, and the flat legacy
/// pollutant lists. Lists live in <c>LandBranchGate.Lib.ps1</c>; legacy excerpts are embedded verbatim (docs/source is
/// not in the repository). No production values.
/// </summary>
/// <remarks>
/// Mutations (this class only): drop '6493' from ECW_C11_16 type2 in the Lib - <see cref="Branch_lists_equal_legacy"/>
/// goes red; drop '1716' from the C11_11_04 substance list - <see cref="Substance_lists_equal_legacy"/>; put '0804' into
/// C11_11_03 Gsec.type3 - <see cref="Gate_selects_the_same_branch_as_the_parity_harness"/> (two branches for one NAE).
/// </remarks>
public sealed class LandBranchGateTests
{
    // ECW_C11_11_04.cs:71-91, ECW_C11_16.cs:70-77, ECW_C11_11_03.cs:82-90, ECW_C11_12_03.cs:72-74, ECW_C11_17.cs:68-70,
    // ECW_C11_18.cs:70-72 - verbatim lines.
    private const string LegacyC111104 = """
        { "333" },
        { "415" },
        { "416" },
        { "602" },
        { "616" },
        { "621" },
        { "1716"}
        { "type1", new List<string> { "415", "416", "602", "616", "621" } },
        { "type2", new List<string> { "333" } },
        { "type3", new List<string> { "1716" } }
        """;

    private const string LegacyC1116 = """
        { "331" }
        { "type1", new List<string> { "6491", "6482(004)", "6482(005)" } },
        { "type2", new List<string> { "6493" } }
        """;

    private const string LegacyC111103Tons = """
        { "type1", new List<string> { "0800(001)", "0800(002)", "0800(003)", "0800(004)", "0800(005)", "0800(006)", "0800(007)", "6800" } },
        { "type2", new List<string> { "0782(001)", "0782(002)", "0782(003)", "0782(004)", "0782(005)", "0782(006)", "0782(007)", "0782(008)", "6784" } },
        { "type3", new List<string> { "0804", "6801", "6802", "6803" } }
        """;

    private const string LegacyC111103Gsec = """
        { "type1", new List<string> { "0800(001)", "0800(002)", "0800(003)", "0800(004)", "0800(005)", "0800(006)", "0800(007)", "6800" } },
        { "type2", new List<string> { "0782(001)", "0782(002)", "0782(003)", "0782(004)", "0782(005)", "0782(006)", "0782(007)", "0782(008)", "6784", "0804", "6801", "6802", "6803" } }
        """;

    private static string Run(string body)
    {
        var dir = Directory.CreateTempSubdirectory("ecr-branchgate-");
        try
        {
            var script = Path.Combine(dir.FullName, "probe.ps1");
            var sb = new StringBuilder("$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n");
            foreach (var lib in new[] { "LandBranchGate.Lib.ps1", "LandParity.Lib.ps1" })
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

    private static Dictionary<string, string[]> Groups(string excerpt)
    {
        var d = new Dictionary<string, string[]>();
        foreach (Match m in Regex.Matches(excerpt, @"\{\s*""(type\d)"",\s*new List<string>\s*\{([^}]*)\}"))
        {
            d[m.Groups[1].Value] = Regex.Matches(m.Groups[2].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToArray();
        }

        return d;
    }

    private static string[] Codes(string excerpt) =>
        Regex.Matches(excerpt, @"^\s*\{\s*""(\d+)""\s*\}", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();

    private static string[] Arr(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!).ToArray() : [e.GetString()!];

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Branch_lists_equal_legacy()
    {
        var json = Run(
            """
            $o = [ordered]@{}
            foreach ($m in @('ECW_C11_11_04', 'ECW_C11_16', 'ECW_C11_11_03')) {
              foreach ($metric in @('tons', 'gsec')) {
                foreach ($t in @('type1', 'type2', 'type3')) {
                  $v = Get-LandBranchGateValues -Methodology $m -FormulaCode "ECW_EC_${metric}_XXX_$t"
                  $o["$m|$metric|$t"] = @($v.Values)
                }
              }
            }
            $o | ConvertTo-Json -Compress -Depth 4
            """);
        using var doc = JsonDocument.Parse(json);
        var g = doc.RootElement;

        void Check(string methodology, string metric, Dictionary<string, string[]> legacy)
        {
            foreach (var t in new[] { "type1", "type2", "type3" })
            {
                var expected = legacy.TryGetValue(t, out var list) ? list : [];
                Assert.Equal(expected, Arr(g.GetProperty($"{methodology}|{metric}|{t}")));
            }
        }

        Check("ECW_C11_11_04", "tons", Groups(LegacyC111104));
        Check("ECW_C11_11_04", "gsec", Groups(LegacyC111104));
        Check("ECW_C11_16", "tons", Groups(LegacyC1116));
        Check("ECW_C11_16", "gsec", Groups(LegacyC1116));
        Check("ECW_C11_11_03", "tons", Groups(LegacyC111103Tons));
        Check("ECW_C11_11_03", "gsec", Groups(LegacyC111103Gsec));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Substance_lists_equal_legacy()
    {
        var json = Run("Get-LandBranchGateSubstances | ConvertTo-Json -Compress -Depth 3");
        using var doc = JsonDocument.Parse(json);
        var s = doc.RootElement;
        Assert.Equal(Codes(LegacyC111104), Arr(s.GetProperty("ECW_C11_11_04")));
        Assert.Equal(Codes(LegacyC1116), Arr(s.GetProperty("ECW_C11_16")));
        Assert.Equal(["333"], Arr(s.GetProperty("ECW_C11_12_03")));
        Assert.Equal(["331"], Arr(s.GetProperty("ECW_C11_17")));
        Assert.Equal(["331"], Arr(s.GetProperty("ECW_C11_18")));
        Assert.Equal(5, s.EnumerateObject().Count());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Gate_selects_the_same_branch_as_the_parity_harness()
    {
        // Every NAE / stream the harness knows: the gate lets through exactly the branch Get-LandParitySelectedBranch
        // picks (and nothing for a value outside the legacy groups); Get-LandSelectedBranchByGate throws on two branches.
        var json = Run(
            """
            $diff = New-Object System.Collections.Generic.List[string]
            $cases = @(
              @{ m = 'ECW_C11_02'; vals = @($script:LandParityC1102Type1 + $script:LandParityC1102Type2 + @('9999')) },
              @{ m = 'ECW_C11_11_02'; vals = @($script:LandParityC111102Known + @('9999')) },
              @{ m = 'ECW_C11_11_03'; vals = @(@($script:LandParityC111103Tons.Values | % { $_ }) + @($script:LandParityC111103Gsec.Values | % { $_ }) + @('9999')) }
            )
            $n = 0
            foreach ($c in $cases) {
              foreach ($v in ($c.vals | Sort-Object -Unique)) {
                foreach ($metric in @('tons', 'gsec')) {
                  $want = Get-LandParitySelectedBranch -MethodologyCode $c.m -MetricName $metric -Row @{ Land_NAE = $v }
                  $got = Get-LandSelectedBranchByGate -Methodology $c.m -Metric $metric -Value $v
                  if ("$want" -cne "$got") { $diff.Add("$($c.m)|$metric|$v harness=$want gate=$got") }
                  $n++
                }
              }
            }
            foreach ($s in @('Fuel gas', '1D-114', 'Diesel', '')) {
              $want = Get-LandParitySelectedBranch -MethodologyCode 'ECW_C12_01' -MetricName 'tons' -Row @{ Land_Streams = $s }
              $got = Get-LandSelectedBranchByGate -Methodology 'ECW_C12_01' -Metric 'tons' -Value $s
              if ("$want" -cne "$got") { $diff.Add("ECW_C12_01|$s harness=$want gate=$got") }
              $n++
            }
            [ordered]@{ n = $n; diff = @($diff) } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("n").GetInt32() > 100);
        Assert.Empty(doc.RootElement.GetProperty("diff").EnumerateArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Golden_test_moves_to_an_existing_document_and_C11_02_gets_a_fuel_gas_stream()
    {
        var json = Run(
            """
            $in = '{"documentId":1,"periodKey":{"value":202606},"arguments":[{"argumentCode":"Land_NAE","value":null,"valueString":"6640"},{"argumentCode":"Land_TypeFuelOrStream","value":null,"valueString":"1-204 sludge"}]}'
            $ov = (Get-LandBranchGateTestOverrides)['ECW_C11_02']
            $a = Convert-LandBranchGateTestInput -InputJson $in -DocumentId 96 -PeriodKey 202609 -Overrides $ov
            $b = Convert-LandBranchGateTestInput -InputJson $a.InputJson -DocumentId 96 -PeriodKey 202609 -Overrides $ov
            [ordered]@{ changed = $a.Changed; again = $b.Changed; input = $a.InputJson } | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.True(r.GetProperty("changed").GetBoolean());
        Assert.False(r.GetProperty("again").GetBoolean());
        using var input = JsonDocument.Parse(r.GetProperty("input").GetString()!);
        Assert.Equal(96, input.RootElement.GetProperty("documentId").GetInt64());
        Assert.Equal(202609, input.RootElement.GetProperty("periodKey").GetProperty("value").GetInt32());
        var stream = input.RootElement.GetProperty("arguments").EnumerateArray()
            .Single(a => a.GetProperty("argumentCode").GetString() == "Land_TypeFuelOrStream").GetProperty("valueString").GetString();
        Assert.Equal("Fuel Gas (Internal) - Топливный газ (Внутренний)", stream);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Wrapper_is_idempotent_adds_argument_and_gives_NULL_for_an_empty_branch()
    {
        var json = Run(
            """
            $r = [ordered]@{}
            $a = Convert-LandBranchGateFormula -Expression 'X*2' -ArgumentsCsv '@X;' -Selector nae -Values @('6482(004)', '6583 (002)')
            $r.nae = $a.Expression; $r.naeArgs = $a.ArgumentsCsv
            $b = Convert-LandBranchGateFormula -Expression $a.Expression -ArgumentsCsv $a.ArgumentsCsv -Selector nae -Values @('6482(004)', '6583 (002)')
            $r.again = $b.Changed
            $c = Convert-LandBranchGateFormula -Expression 'X*2' -ArgumentsCsv '@X;' -Selector substance -Values @('333')
            $r.subst = $c.Expression; $r.substArgs = $c.ArgumentsCsv
            $d = Convert-LandBranchGateFormula -Expression 'X*2' -ArgumentsCsv '@X;' -Selector stream -Values @('!Fuel gas')
            $r.stream = $d.Expression; $r.streamArgs = $d.ArgumentsCsv
            $e = Convert-LandBranchGateFormula -Expression 'X*2' -ArgumentsCsv '@X;' -Selector nae -Values @()
            $r.empty = $e.Expression
            $f = Convert-LandBranchGateFormula -Expression 'NULL' -ArgumentsCsv '@X;' -Selector nae -Values @()
            $r.emptyAgain = $f.Changed
            $r.gsecType3 = @((Get-LandBranchGateValues -Methodology 'ECW_C11_11_03' -FormulaCode 'ECW_EC_gsec_XXX_type3').Values).Count
            $r.notGated = ($null -eq (Get-LandBranchGateValues -Methodology 'ECW_C11_10' -FormulaCode 'ECW_EC_tons_XXX_type1'))
            $r | ConvertTo-Json -Compress
            """);
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal("if(in(@Land_NAE, '6482(004)', '6482 (004)'), X*2, NULL)", r.GetProperty("nae").GetString());
        Assert.Equal("@X;@Land_NAE;", r.GetProperty("naeArgs").GetString());
        Assert.False(r.GetProperty("again").GetBoolean());
        Assert.Equal("if(in(CURRENT_SUBSTANCE(), '333'), X*2, NULL)", r.GetProperty("subst").GetString());
        Assert.Equal("@X;", r.GetProperty("substArgs").GetString());
        Assert.Equal("if(@Land_Streams <> 'Fuel gas', X*2, NULL)", r.GetProperty("stream").GetString());
        Assert.Equal("@X;@Land_Streams;", r.GetProperty("streamArgs").GetString());
        Assert.Equal("NULL", r.GetProperty("empty").GetString());
        Assert.False(r.GetProperty("emptyAgain").GetBoolean());
        Assert.Equal(0, r.GetProperty("gsecType3").GetInt32());
        Assert.True(r.GetProperty("notGated").GetBoolean());
    }
}
