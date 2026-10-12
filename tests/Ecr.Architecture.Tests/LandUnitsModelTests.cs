using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// RC21 F(3): <c>units.new</c> моделі шаблону Land збігається з секцією <c>-- LAND:units</c> сіду (G8).
/// Множник — до БАЗОВОЇ одиниці розмірності (kg/s для MassFlow), а не до <c>dimensionOf</c>: раніше kg/h і
/// t/h стояли в g/s і були в 1000 разів завеликі, і Build-LandTemplate заводив би їх помилково.
/// </summary>
public sealed partial class LandUnitsModelTests
{
    /// <summary>Очікування з секції LAND:units (lane/land-g8-units, коміт e95947fbe): код → (розмірність, множник).</summary>
    private static readonly Dictionary<string, (int Dimension, decimal Factor)> Expected = new(StringComparer.Ordinal)
    {
        ["kg_per_h"] = (8, 0.000277777777777778m),
        ["t_per_h"] = (8, 0.277777777777777778m),
        ["mg_per_l"] = (11, 0.001m),
        ["thousand_m3"] = (2, 1000m),
    };

    /// <summary>Розмірність одиниці, на яку посилається <c>dimensionOf</c> (каталог 09-seed.sql, блок HSE301/базовий).</summary>
    private static readonly Dictionary<string, int> DimensionOfUnit = new(StringComparer.Ordinal)
    {
        ["g_per_s"] = 8, ["kg_per_m3"] = 11, ["m3"] = 2,
    };

    private static JsonElement NewUnits(JsonDocument model)
        => model.RootElement.GetProperty("units").GetProperty("new");

    private static JsonDocument Model()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            SourceTree.Root, "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Нові_одиниці_моделі_мають_множники_до_бази_розмірності_як_у_сіді_G8()
    {
        using var model = Model();
        var units = NewUnits(model).EnumerateArray().ToDictionary(u => u.GetProperty("code").GetString()!);

        Assert.Equal(Expected.Keys.Order(StringComparer.Ordinal), units.Keys.Order(StringComparer.Ordinal));

        foreach (var (code, (dimension, factor)) in Expected)
        {
            var unit = units[code];
            Assert.Equal(dimension, DimensionOfUnit[unit.GetProperty("dimensionOf").GetString()!]);
            Assert.Equal(
                factor,
                decimal.Parse(unit.GetProperty("factor").GetString()!, CultureInfo.InvariantCulture));
        }

        // Фізичний сенс: kg/h = 1/3600 kg/s; t/h = 1000 kg/h (а не 1000 g/s на годину).
        Assert.Equal(1m / 3600m, Expected["kg_per_h"].Factor, 15);
        Assert.Equal(Expected["kg_per_h"].Factor * 1000m, Expected["t_per_h"].Factor, 15);
    }

    [GeneratedRegex(@"\(N'(?<code>kg_per_h|t_per_h|mg_per_l)',\s*(?<dim>\d+),\s*N'[a-z0-9]+',\s*N'[a-z0-9]+',\s*(?<f>[0-9.]+)\)")]
    private static partial Regex DerivedRow();

    [GeneratedRegex(@"\(N'thousand_m3',\s*(?<dim>\d+),\s*\d+,\s*(?<f>[0-9.]+),")]
    private static partial Regex ThousandRow();

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Коли_секція_LAND_units_є_в_сіді_множники_моделі_дорівнюють_сідовим()
    {
        // ⚠ Секція `-- LAND:units` живе в lane/land-g8-units (e95947fbe) і доїде в dev/integration окремо; у
        // базі цієї lane її ще немає. Тест не вимикається (honesty-guard): без секції порівнювати нічого, а з
        // нею стає суворим автоматично — розбіжність моделі з сідом тоді червоніє.
        var seed = File.ReadAllText(Path.Combine(
            SourceTree.Root, "src", "Ecr.Infrastructure", "Persistence", "Sql", "09-seed.sql"));
        var start = seed.IndexOf("-- LAND:units", StringComparison.Ordinal);
        if (start < 0)
        {
            return;
        }

        var end = seed.IndexOf("-- LAND:units ── кінець секції", start, StringComparison.Ordinal);
        Assert.True(end > start, "Секція LAND:units без кінцевого маркера.");
        var section = seed[start..end];

        using var model = Model();
        var units = NewUnits(model).EnumerateArray().ToDictionary(
            u => u.GetProperty("code").GetString()!,
            u => (Dimension: DimensionOfUnit[u.GetProperty("dimensionOf").GetString()!],
                  Factor: decimal.Parse(u.GetProperty("factor").GetString()!, CultureInfo.InvariantCulture)));

        var seeded = new Dictionary<string, (int Dimension, decimal Factor)>(StringComparer.Ordinal);
        foreach (Match m in DerivedRow().Matches(section))
        {
            seeded[m.Groups["code"].Value] = (int.Parse(m.Groups["dim"].Value, CultureInfo.InvariantCulture),
                decimal.Parse(m.Groups["f"].Value, CultureInfo.InvariantCulture));
        }

        if (ThousandRow().Match(section) is { Success: true } t)
        {
            seeded["thousand_m3"] = (int.Parse(t.Groups["dim"].Value, CultureInfo.InvariantCulture),
                decimal.Parse(t.Groups["f"].Value, CultureInfo.InvariantCulture));
        }

        Assert.Equal(units.Keys.Order(StringComparer.Ordinal), seeded.Keys.Order(StringComparer.Ordinal));
        Assert.All(units, kv => Assert.Equal(seeded[kv.Key], kv.Value));
    }
}
