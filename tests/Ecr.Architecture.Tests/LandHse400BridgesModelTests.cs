using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// LP-10 (HSE400): склад газу для методологій Land C11/C12 - два довідники (<c>LAND_HSE400_STREAM</c>,
/// <c>LAND_HSE400_COMPOSITION</c>, складений ключ рік/місяць/потік/показник) і мости аргументів Density20, Vddg ...
/// на REGFIND/REGFIELD. Нових функцій рушія й міграцій немає. Мости - лише там, де колонка-джерело потоку однозначна;
/// решта (і склад по речовині CompositionMass) - в <c>argumentBridges.unresolved</c> з причиною. Значень складу в моделі
/// немає.
/// </summary>
public sealed class LandHse400BridgesModelTests
{
    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static readonly string[] HseArguments =
    [
        "CompositionMass", "Density20", "Density0", "Density", "Vddg", "Vwdg",
        "TotalSulphurContent", "MercaptanSulphurContent", "FuelLowerHeating", "H2Smass",
    ];

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, name)));

    private static Dictionary<string, JsonElement> Tables(JsonDocument model)
        => model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .GroupBy(t => t.GetProperty("code").GetString()!)
            .ToDictionary(g => g.Key, g => g.First());

    private static Dictionary<string, JsonElement> Columns(JsonElement table)
        => table.GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!, c => c);

    private static string Expression(JsonDocument model, string table, string column)
        => Columns(Tables(model)[table])[column].GetProperty("expression").GetString()!;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Довідники_мають_типізовані_поля_і_складений_первинний_ключ()
    {
        using var model = Load("land-template.model.json");
        var registries = model.RootElement.GetProperty("registries").EnumerateArray()
            .ToDictionary(r => r.GetProperty("code").GetString()!, r => r);

        var composition = registries["LAND_HSE400_COMPOSITION"];
        var fields = composition.GetProperty("fieldSpecs").EnumerateArray()
            .ToDictionary(f => f.GetProperty("code").GetString()!, f => f.GetProperty("dataType").GetString()!);
        Assert.Equal("Int", fields["YEAR"]);
        Assert.Equal("Int", fields["MONTH"]);
        Assert.Equal("String", fields["STREAM"]);
        Assert.Equal("String", fields["INDICATOR"]);
        Assert.Equal("Decimal", fields["VALUE"]);

        var key = Assert.Single(composition.GetProperty("keys").EnumerateArray());
        Assert.True(key.GetProperty("isPrimary").GetBoolean());
        Assert.Equal(
            ["YEAR", "MONTH", "STREAM", "INDICATOR"],
            key.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray());

        var stream = registries["LAND_HSE400_STREAM"];
        var streamKey = Assert.Single(stream.GetProperty("keys").EnumerateArray());
        Assert.Equal(["TEXT"], streamKey.GetProperty("fields").EnumerateArray().Select(f => f.GetString()!).ToArray());
        Assert.Contains(
            stream.GetProperty("entries").EnumerateArray(),
            e => e.GetProperty("fields").GetProperty("PREFIX").GetString() == "LPG_Internal");
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Міст_вибирає_склад_за_роком_місяцем_потоком_із_тексту_палива_і_показником()
    {
        using var model = Load("land-template.model.json");

        Assert.Equal(
            "REGFIELD(REGFIND('LAND_HSE400_COMPOSITION', [Period].Year, [Period].Sequence, "
            + "REGFIELD(REGFIND('LAND_HSE400_STREAM', [TypeOfFuel]), 'PREFIX'), 'Density20'), 'VALUE')",
            Expression(model, "T9_04", "Density20"));

        // Показник - канонічне ім'я суфікса колонки прода, а не ім'я аргументу.
        Assert.Contains("'FuelLowerHeating20'", Expression(model, "T9_08", "FuelLowerHeating"), StringComparison.Ordinal);
        Assert.Contains("'H2S_mass'", Expression(model, "T9_08", "H2Smass"), StringComparison.Ordinal);
        Assert.Contains("'Density20'", Expression(model, "T9_08", "Density"), StringComparison.Ordinal);
        Assert.Contains("'TotalSulphurContent'", Expression(model, "T9_07", "TotalSulphurContent"), StringComparison.Ordinal);

        // Lookup-джерело потоку (C12_03): текст - назва запису довідника палива.
        Assert.Contains(
            "REGFIND('LAND_HSE400_STREAM', REGFIELD([Land_TypeFuel], 'NAME'))",
            Expression(model, "T10_01", "Density20"),
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожен_міст_HSE400_це_Formula_лише_для_читання_і_читає_колонку_своєї_таблиці_і_довідники_моделі()
    {
        using var model = Load("land-template.model.json");
        var tables = Tables(model);
        var registries = model.RootElement.GetProperty("registries").EnumerateArray()
            .Select(r => r.GetProperty("code").GetString()!).ToHashSet();
        var bridged = model.RootElement.GetProperty("argumentBridges").GetProperty("bridged");
        var total = 0;

        foreach (var table in bridged.EnumerateObject())
        {
            var cols = Columns(tables[table.Name]);
            foreach (var name in table.Value.EnumerateArray().Select(x => x.GetString()!).Where(HseArguments.Contains))
            {
                total++;
                var c = cols[name];
                Assert.Equal("Formula", c.GetProperty("dataType").GetString());
                Assert.True(c.GetProperty("readOnly").GetBoolean());
                var expr = c.GetProperty("expression").GetString()!;

                foreach (Match m in Regex.Matches(expr, @"REG(?:FIND|ONE)\('([^']+)'"))
                {
                    Assert.Contains(m.Groups[1].Value, registries);
                }

                foreach (Match m in Regex.Matches(expr, @"\[([^\]]+)\]"))
                {
                    var token = m.Groups[1].Value;
                    Assert.True(token.StartsWith("Period", StringComparison.Ordinal) || cols.ContainsKey(token),
                        $"{table.Name}.{name}: [{token}] не колонка таблиці.");
                }
            }
        }

        // 19 мостів: T9_02 1, T9_04 4, T9_07 3, T9_08 4, T9_10 4, T9_11 1, T10_01 1, T11_01 1.
        Assert.Equal(19, total);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Склад_по_речовині_і_таблиці_без_однозначного_джерела_потоку_названі_в_unresolved_з_причиною()
    {
        using var model = Load("land-template.model.json");
        var tables = Tables(model);
        var bridges = model.RootElement.GetProperty("argumentBridges");
        var unresolved = bridges.GetProperty("unresolved");

        // CompositionMass - по речовині: не міст, а питання (дані в довіднику є).
        foreach (var table in new[] { "T9_04", "T9a_16", "T10_01", "T11_02" })
        {
            var reason = unresolved.GetProperty(table).GetProperty("CompositionMass").GetString()!;
            Assert.Contains("речовин", reason, StringComparison.Ordinal);
            Assert.DoesNotContain("CompositionMass", Columns(tables[table]).Keys);
        }

        // 9a: і Stream, і TypeOfStream - джерело потоку не доведене, нічого не вгадуємо.
        var t9a = unresolved.GetProperty("T9a_25");
        Assert.Contains("Stream", t9a.GetProperty("Density20").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("Density20", Columns(tables["T9a_25"]).Keys);

        // C12_02: селектор «Land_TypeStream, інакше Land_TypeFuel».
        Assert.Contains("C12_02", unresolved.GetProperty("T10_03").GetProperty("Vddg").GetString()!, StringComparison.Ordinal);

        // Правило «немає запису» - одне, записане в моделі.
        Assert.Contains(
            "#N/A",
            bridges.GetProperty("hse400").GetProperty("rule").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Колонка_джерело_потоку_існує_й_відповідає_типу_і_кожен_аргумент_HSE400_або_міст_або_unresolved()
    {
        using var model = Load("land-template.model.json");
        using var map = Load("land-result-outputs.json");
        var tables = Tables(model);
        var unresolved = model.RootElement.GetProperty("argumentBridges").GetProperty("unresolved");

        foreach (var source in model.RootElement.GetProperty("argumentBridges").GetProperty("hse400")
                     .GetProperty("streamSourceColumn").EnumerateObject())
        {
            Assert.True(Columns(tables[source.Name]).ContainsKey(source.Value.GetString()!), source.Name);
        }

        var pairs = 0;
        foreach (var m in map.RootElement.GetProperty("methodologies").EnumerateObject())
        {
            foreach (var t in m.Value.GetProperty("tables").EnumerateArray().Select(x => x.GetString()!))
            {
                if (!tables.TryGetValue(t, out var table))
                {
                    continue;
                }

                var cols = Columns(table);
                foreach (var input in m.Value.GetProperty("inputs").EnumerateArray().Select(x => x.GetString()!)
                             .Where(HseArguments.Contains))
                {
                    pairs++;
                    var named = unresolved.TryGetProperty(t, out var u) && u.TryGetProperty(input, out _);
                    Assert.True(
                        cols.ContainsKey(input) ^ named,
                        $"{t}: вхід {input} ({m.Name}) має бути або мостом, або в unresolved (не обидва і не жодне).");
                }
            }
        }

        Assert.True(pairs >= 56, $"Входів HSE400 має бути ≥ 56, зараз {pairs}.");
    }
}
