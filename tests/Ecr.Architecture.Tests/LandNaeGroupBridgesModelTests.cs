using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-10 (залишок 0438): похідні аргументи методологій Land, які старий код виводить зі словників у C#:
/// <c>Land_Power</c> (<c>ECW_C11_03</c>, 9a_17) і <c>Category1</c> (<c>ECW_C11_11_02</c>) - довідник
/// <c>LAND_NAE_GROUP</c> і мости REGFIND/REGFIELD; <c>Land_MassFraction</c> (<c>ECW_C11_11_04</c>) - мости
/// <c>Land_MassFraction_0&lt;код&gt;</c> і фрагмент за <c>CURRENT_SUBSTANCE()</c>. Рушія й міграцій немає; значень прода
/// в git немає (записи довідника - коди NAE і номери груп зі словників C#).
/// </summary>
/// <remarks>
/// Очікувані словники нижче переписано З C# (ECW_C11_03.cs:81-92, ECW_C11_11_02.cs:127-133, ECW_C11_11_04.cs:82-86) і
/// не залежать від генератора: розбіжність моделі зі старим кодом червоніє.
/// Мутаційні докази (лише цей клас): у <c>af-land-nae-groups.json</c> змінити групу будь-якого NAE і перегенерувати
/// модель — червоніє <see cref="Записи_довідника_збігаються_зі_словниками_старого_коду"/>; у
/// <c>nae_group_bridges.py</c> прибрати запис із пробілом перед дужкою — червоніє те саме.
/// </remarks>
public sealed class LandNaeGroupBridgesModelTests
{
    // ECW_C11_03.cs powerGroup (група потужності -> NAE); виставляється лише для TableName == AF_Air_Land_ProcessUnits_9a_17.
    private static readonly Dictionary<string, string[]> Power = new()
    {
        ["440"] = ["0114"],
        ["448"] = ["0116"],
        ["640"] = ["0603"],
        ["5420"] = ["0662", "0663", "0664"],
        ["9.9"] = ["0801"],
        ["708"] = ["0920", "0921"],
        ["60"] = ["0926"],
        ["2710"] = ["0660", "0661"],
        ["861"] = ["0672", "0673"],
        ["272"] = ["0924"],
    };

    // ECW_C11_11_02.cs group1 (Category1 -> NAE; код знімає пробіли з NAE).
    private static readonly Dictionary<string, string[]> Category1 = new()
    {
        ["0788"] = ["0788", "0789", "0790", "0791", "0788(001)", "0789(001)", "0790(001)", "0791(001)"],
        ["6975"] = ["6975", "6981", "6982", "6983", "6984", "6985", "6986", "6976"],
        ["0560"] = ["0560", "0561", "0562", "0563", "0564", "0565", "0903", "0905", "6904"],
        ["0793"] = ["0793", "0883", "0524"],
    };

    // ECW_C11_11_04.cs typeCalculationGroupTonne.type1 і заголовок колонки Excel «<код>: ...».
    private static readonly (string Code, string Column)[] MassFraction =
    [
        ("415", "SaturatedHydrocarbonsC1C5"),
        ("416", "SaturatedHydrocarbonsC6C10"),
        ("602", "Benzene"),
        ("616", "Xylene"),
        ("621", "Toluene"),
    ];

    private static readonly string[] CategoryTables = ["T9_01", "T9_07", "T9_08", "T9a_16", "T9a_17a", "T9a_19"];

    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static string LandFile(string name) => Path.Combine(SourceTree.Root, "tools", "land", name);

    private static JsonDocument Load(string name = "land-template.model.json")
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, name)));

    private static Dictionary<string, JsonElement> Tables(JsonDocument model)
        => model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .GroupBy(t => t.GetProperty("code").GetString()!)
            .ToDictionary(g => g.Key, g => g.First());

    private static Dictionary<string, JsonElement> Columns(JsonElement table)
        => table.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c);

    private static string? Unresolved(JsonDocument model, string table, string arg)
    {
        var u = model.RootElement.GetProperty("argumentBridges").GetProperty("unresolved");
        return u.TryGetProperty(table, out var t) && t.TryGetProperty(arg, out var r) ? r.GetString() : null;
    }

    private static JsonElement Registry(JsonDocument model)
        => model.RootElement.GetProperty("registries").EnumerateArray()
            .Single(r => r.GetProperty("code").GetString() == "LAND_NAE_GROUP");

    private static (int Exit, string Output) RunPowerShell(string script, params string[] args)
    {
        var start = new ProcessStartInfo(DeployScriptHarness.FindPowerShell())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        var file = Path.Combine(Path.GetTempPath(), $"ecr-land-naegroup-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file }
                         .Concat(args))
            {
                start.ArgumentList.Add(argument);
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
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Довідник_має_типізовані_поля_і_первинний_ключ_NAE()
    {
        using var model = Load();
        var registry = Registry(model);

        var fields = registry.GetProperty("fieldSpecs").EnumerateArray()
            .ToDictionary(f => f.GetProperty("code").GetString()!, f => f.GetProperty("dataType").GetString()!);
        Assert.Equal("String", fields["NAE_NORMALIZED"]);
        Assert.Equal("Decimal", fields["POWER_GROUP"]);
        Assert.Equal("String", fields["CATEGORY1"]);

        var key = Assert.Single(registry.GetProperty("keys").EnumerateArray());
        Assert.True(key.GetProperty("isPrimary").GetBoolean());
        Assert.Equal(["NAE_NORMALIZED"], key.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Записи_довідника_збігаються_зі_словниками_старого_коду()
    {
        using var model = Load();
        var entries = Registry(model).GetProperty("entries").EnumerateArray().ToList();

        var expectedPower = Power.SelectMany(p => p.Value.Select(nae => (Key: nae, Group: p.Key)))
            .ToDictionary(x => x.Key, x => x.Group);

        // Category1: ключ без пробілів (як у коді) і з пробілом перед дужкою (як у шаблоні Excel).
        var expectedCategory = new Dictionary<string, string>();
        foreach (var (group, naes) in Category1)
        {
            foreach (var nae in naes)
            {
                expectedCategory[nae] = group;
                expectedCategory[nae.Replace("(", " (", StringComparison.Ordinal)] = group;
            }
        }

        var actualPower = entries
            .Where(e => e.GetProperty("fields").TryGetProperty("POWER_GROUP", out _))
            .ToDictionary(
                e => e.GetProperty("fields").GetProperty("NAE_NORMALIZED").GetString()!,
                e => e.GetProperty("fields").GetProperty("POWER_GROUP").GetString()!);
        var actualCategory = entries
            .Where(e => e.GetProperty("fields").TryGetProperty("CATEGORY1", out _))
            .ToDictionary(
                e => e.GetProperty("fields").GetProperty("NAE_NORMALIZED").GetString()!,
                e => e.GetProperty("fields").GetProperty("CATEGORY1").GetString()!);

        Assert.Equal(expectedPower.OrderBy(p => p.Key, StringComparer.Ordinal), actualPower.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(expectedCategory.OrderBy(p => p.Key, StringComparer.Ordinal), actualCategory.OrderBy(p => p.Key, StringComparer.Ordinal));

        // 15 NAE потужності + 28 NAE Category1 + 4 написання з пробілом перед дужкою; групи не перетинаються.
        Assert.Equal(15, actualPower.Count);
        Assert.Equal(32, actualCategory.Count);
        Assert.Equal(47, entries.Count);
        Assert.Empty(actualPower.Keys.Intersect(actualCategory.Keys));

        // Ключі унікальні й не збігаються без урахування регістру; код запису - літера + 10 hex.
        Assert.Equal(entries.Count, entries.Select(e => e.GetProperty("fields").GetProperty("NAE_NORMALIZED").GetString()!.ToUpperInvariant()).Distinct().Count());
        Assert.All(entries, e => Assert.Matches("^N[0-9A-F]{10}$", e.GetProperty("code").GetString()!));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Файл_груп_із_C_sharp_збігається_зі_словниками_і_не_містить_прод_даних()
    {
        using var doc = Load("af-land-nae-groups.json");
        var root = doc.RootElement;

        Assert.Equal("T9a_17", root.GetProperty("powerGroup").GetProperty("table").GetString());
        Assert.False(root.GetProperty("powerGroup").GetProperty("normalizesNae").GetBoolean());
        Assert.True(root.GetProperty("category1").GetProperty("normalizesNae").GetBoolean());

        static Dictionary<string, string[]> Groups(JsonElement element)
            => element.GetProperty("groups").EnumerateObject().ToDictionary(
                g => g.Name, g => g.Value.EnumerateArray().Select(v => v.GetString()!).ToArray());

        Assert.Equal(
            Power.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, string.Join(',', p.Value))),
            Groups(root.GetProperty("powerGroup")).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, string.Join(',', p.Value))));
        Assert.Equal(
            Category1.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, string.Join(',', p.Value))),
            Groups(root.GetProperty("category1")).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, string.Join(',', p.Value))));
        Assert.Equal(
            MassFraction.Select(m => m.Code),
            root.GetProperty("massFraction").GetProperty("type1Codes").EnumerateArray().Select(c => c.GetString()!));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Land_Power_і_Category1_є_мостами_через_довідник_а_не_лишаються_в_unresolved()
    {
        using var model = Load();
        var tables = Tables(model);

        // Land_Power - лише 9a_17 (guard TableName у C11_03); колонка NAE - вже зведений міст Land_NAE цієї таблиці.
        var power = Columns(tables["T9a_17"])["Land_Power"];
        Assert.Equal("Formula", power.GetProperty("dataType").GetString());
        Assert.True(power.GetProperty("readOnly").GetBoolean());
        Assert.Equal(
            "REGFIELD(REGFIND('LAND_NAE_GROUP', [IDCodeNAEProject]), 'POWER_GROUP')",
            power.GetProperty("expression").GetString());
        Assert.Equal(34, power.GetProperty("precision").GetInt32());   // число, не текст

        foreach (var (code, table) in tables)
        {
            var cols = Columns(table);
            if (code != "T9a_17" && cols.TryGetValue("Land_Power", out var other)
                && other.TryGetProperty("expression", out var expression))
            {
                // Інші таблиці мають Land_Power як зведений міст за атрибутом AF (колонка «Power, kW»), не через групи NAE.
                Assert.DoesNotContain("LAND_NAE_GROUP", expression.GetString() ?? string.Empty, StringComparison.Ordinal);
            }
        }

        foreach (var table in CategoryTables)
        {
            var cols = Columns(tables[table]);
            var nae = cols["Land_NAE"].GetProperty("expression").GetString()!.Trim('[', ']');
            var category = cols["Category1"];
            Assert.Equal("Formula", category.GetProperty("dataType").GetString());
            Assert.True(category.GetProperty("readOnly").GetBoolean());
            Assert.Equal(
                $"REGFIELD(REGFIND('LAND_NAE_GROUP', [{nae}]), 'CATEGORY1')",
                category.GetProperty("expression").GetString());
            Assert.Null(Unresolved(model, table, "Category1"));
        }

        Assert.Null(Unresolved(model, "T9a_17", "Land_Power"));

        var meta = model.RootElement.GetProperty("argumentBridges").GetProperty("naeGroup");
        Assert.Equal("LAND_NAE_GROUP", meta.GetProperty("registry").GetString());
        Assert.Equal(CategoryTables, meta.GetProperty("category1Tables").EnumerateArray().Select(t => t.GetString()!).ToArray());
        Assert.Contains("#N/A", meta.GetProperty("rule").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Land_MassFraction_має_п_ять_мостів_за_кодом_у_заголовку_і_фрагмент_за_речовиною()
    {
        using var model = Load();
        var tables = Tables(model);
        var cols = Columns(tables["T9a_19"]);

        foreach (var (code, column) in MassFraction)
        {
            var arg = $"Land_MassFraction_0{code}";
            Assert.StartsWith($"{code}:", cols[column].GetProperty("headerEn").GetString(), StringComparison.Ordinal);
            Assert.Equal("Formula", cols[arg].GetProperty("dataType").GetString());
            Assert.True(cols[arg].GetProperty("readOnly").GetBoolean());
            Assert.Equal($"[{column}]", cols[arg].GetProperty("expression").GetString());
        }

        // Колонки без коду речовини немає: аргумент вибирає фрагмент у формулі методології.
        Assert.DoesNotContain("Land_MassFraction", cols.Keys);
        Assert.Contains("CURRENT_SUBSTANCE", Unresolved(model, "T9a_19", "Land_MassFraction")!, StringComparison.Ordinal);

        var registries = model.RootElement.GetProperty("registries").EnumerateArray()
            .Select(r => r.GetProperty("code").GetString()!).ToHashSet();
        var spec = model.RootElement.GetProperty("argumentBridges").GetProperty("massFraction")
            .GetProperty("methodologies").GetProperty("ECW_C11_11_04");
        Assert.Equal("Land_MassFraction", spec.GetProperty("token").GetString());
        var fragment = spec.GetProperty("fragment").GetString()!;
        Assert.DoesNotContain("@Land_MassFraction,", fragment, StringComparison.Ordinal);
        foreach (var (code, _) in MassFraction)
        {
            Assert.Contains($"CURRENT_SUBSTANCE() = '{code}', @Land_MassFraction_0{code}", fragment, StringComparison.Ordinal);
        }

        foreach (Match m in Regex.Matches(fragment, @"REG(?:FIND|ONE)\('([^']+)'"))
        {
            Assert.Contains(m.Groups[1].Value, registries);
        }

        Assert.Equal(
            MassFraction.Select(m => $"Land_MassFraction_0{m.Code}"),
            spec.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()!));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void План_довідника_береться_з_моделі_і_відкидає_зіпсовані_записи()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $model = Get-Content -LiteralPath $args[1] -Raw -Encoding UTF8 | ConvertFrom-Json
            $reg = @($model.registries) | Where-Object { $_.code -eq 'LAND_NAE_GROUP' } | Select-Object -First 1
            $plan = ConvertTo-LandNaeGroupPlan -Registry $reg
            'COUNT=' + @($plan.Items).Count
            'SKIPPED=' + $plan.Skipped
            'POWER=' + @($plan.Items | Where-Object { $null -ne $_.Power }).Count
            'CAT=' + @($plan.Items | Where-Object { $null -ne $_.Category1 }).Count
            $x = @($plan.Items | Where-Object { $_.Key -eq '0801' })[0]
            'P0801=' + $x.Power.ToString([cultureinfo]::InvariantCulture)
            $s = @($plan.Items | Where-Object { $_.Key -eq '0788 (001)' })[0]
            'SPACED=' + $s.Category1
            $bad = [pscustomobject]@{ entries = @(
                [pscustomobject]@{ code = 'N0123456789'; fields = [pscustomobject]@{ NAE_NORMALIZED = '0114'; POWER_GROUP = '440' } },
                [pscustomobject]@{ code = 'N0123456789'; fields = [pscustomobject]@{ NAE_NORMALIZED = '0114'; POWER_GROUP = '448' } },
                [pscustomobject]@{ code = 'N0123456780'; fields = [pscustomobject]@{ NAE_NORMALIZED = '01 14'; POWER_GROUP = '440' } },
                [pscustomobject]@{ code = 'N0123456781'; fields = [pscustomobject]@{ NAE_NORMALIZED = '0116'; POWER_GROUP = 'abc' } },
                [pscustomobject]@{ code = 'N0123456782'; fields = [pscustomobject]@{ NAE_NORMALIZED = '0603' } },
                [pscustomobject]@{ code = 'bad'; fields = [pscustomobject]@{ NAE_NORMALIZED = '0801'; POWER_GROUP = '9.9' } }) }
            $p2 = ConvertTo-LandNaeGroupPlan -Registry $bad
            'BAD_COUNT=' + @($p2.Items).Count
            'BAD_SKIPPED=' + $p2.Skipped
            """;

        var (exit, output) = RunPowerShell(
            script, LandFile("LandNaeGroup.Lib.ps1"), Path.Combine(Dir, "land-template.model.json"));

        Assert.True(exit == 0, output);
        Assert.Contains("COUNT=47", output, StringComparison.Ordinal);
        Assert.Contains("SKIPPED=0", output, StringComparison.Ordinal);
        Assert.Contains("POWER=15", output, StringComparison.Ordinal);
        Assert.Contains("CAT=32", output, StringComparison.Ordinal);
        Assert.Contains("P0801=9.9", output, StringComparison.Ordinal);
        Assert.Contains("SPACED=0788", output, StringComparison.Ordinal);
        Assert.Contains("BAD_COUNT=1", output, StringComparison.Ordinal);
        Assert.Contains("BAD_SKIPPED=5", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Підстановка_Land_MassFraction_не_чіпає_Land_MassFraction_0_код_і_є_ідемпотентною()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            . $args[0]
            $frag = "if(CURRENT_SUBSTANCE() = '415', @Land_MassFraction_0415, REGFIELD(REGFIND('S', 'x'), 'I'))"
            $args415 = @('Land_MassFraction_0415')
            $r = Convert-LandHse400CompositionMassFormula -Expression '@Land_MassFraction * @Land_Measure_Density20 + @Land_MassFraction_0415' `
                -ArgumentsCsv "@Land_MassFraction;@Land_Measure_Density20;@Land_MassFraction_0415;" -Fragment $frag -StreamArguments $args415 -ArgName 'Land_MassFraction'
            'CHANGED=' + $r.Changed
            'EXPR=' + $r.Expression
            'ARGS=' + $r.ArgumentsCsv
            $only = Convert-LandHse400CompositionMassFormula -Expression '@Land_MassFraction_0415 + 1' -ArgumentsCsv '@Land_MassFraction_0415;' `
                -Fragment $frag -ArgName 'Land_MassFraction'
            'ONLY_SUFFIXED=' + $only.Changed
            $again = Convert-LandHse400CompositionMassFormula -Expression $r.Expression -ArgumentsCsv $r.ArgumentsCsv -Fragment $frag -StreamArguments $args415 -ArgName 'Land_MassFraction'
            'AGAIN=' + $again.Changed
            $legacy = Convert-LandHse400CompositionMassFormula -Expression '@CompositionMass + 1' -ArgumentsCsv '@CompositionMass;' -Fragment 'F'
            'DEFAULT_TOKEN=' + $legacy.Changed
            """;

        var (exit, output) = RunPowerShell(script, LandFile("LandHse400Composition.Lib.ps1"));

        Assert.True(exit == 0, output);
        Assert.Contains("CHANGED=True", output, StringComparison.Ordinal);
        Assert.Contains(
            "EXPR=if(CURRENT_SUBSTANCE() = '415', @Land_MassFraction_0415, REGFIELD(REGFIND('S', 'x'), 'I')) * @Land_Measure_Density20 + @Land_MassFraction_0415",
            output,
            StringComparison.Ordinal);
        Assert.Contains("ARGS=@Land_Measure_Density20;@Land_MassFraction_0415;", output, StringComparison.Ordinal);
        Assert.Contains("ONLY_SUFFIXED=False", output, StringComparison.Ordinal);
        Assert.Contains("AGAIN=False", output, StringComparison.Ordinal);
        Assert.Contains("DEFAULT_TOKEN=True", output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Скрипт_довідника_NAE_відмовляє_на_чужому_API_і_без_підтвердження_стенда_і_не_читає_прод()
    {
        var import = LandFile("Import-LandNaeGroup.ps1");

        var (remoteExit, remote) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'https://ecr.example.com' -ConfirmStand 'ecr.example.com' -DryRun; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, remoteExit);
        Assert.Contains("localhost", remote, StringComparison.Ordinal);

        var (standExit, stand) = RunPowerShell(
            "try { & $args[0] -BaseUrl 'http://localhost:5094'; exit 0 } catch { Write-Output $_.Exception.Message; exit 7 }",
            import);
        Assert.Equal(7, standExit);
        Assert.Contains("-ConfirmStand", stand, StringComparison.Ordinal);

        var text = File.ReadAllText(import);
        Assert.DoesNotContain("SqlConnection", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dbo.", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("Assert-LocalBaseUrl $BaseUrl", StringComparison.Ordinal)
            < text.IndexOf("Connect-Ecr -BaseUrl", StringComparison.Ordinal),
            "Локальність API перевіряється до входу в стенд.");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Етап_MassFraction_лише_явний_ставить_Strict_і_не_чіпає_опубліковані_версії()
    {
        var text = File.ReadAllText(LandFile("Build-LandTemplate.ps1"));

        Assert.Contains("'Data', 'MassFraction', 'LandParity', 'CompositionMass', 'All')", text, StringComparison.Ordinal);
        Assert.Contains(
            "if ($Stage -contains 'MassFraction' -or $Stage -contains 'CompositionMass') { Stage-MassFraction }",
            text,
            StringComparison.Ordinal);

        var start = text.IndexOf("function Stage-MassFraction", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = text[start..text.IndexOf("function Stage-CompositionMass", start, StringComparison.Ordinal)];
        Assert.Contains("-eq 'Published'", body, StringComparison.Ordinal);
        Assert.Contains("-ArgName ([string]$p.Value.token)", body, StringComparison.Ordinal);
        Assert.Contains("numericMode = 'Strict'", body, StringComparison.Ordinal);
        Assert.DoesNotContain("/publish", body, StringComparison.Ordinal);
    }
}
