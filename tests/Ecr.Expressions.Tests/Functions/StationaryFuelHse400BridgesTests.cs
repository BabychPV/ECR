// tests/Ecr.Expressions.Tests/Functions/StationaryFuelHse400BridgesTests.cs
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Expressions.Tests.Functions;

/// <summary>
/// B89 (рішення людини 13.11, відповідь старої системи): на T3_00 чотири мости HSE400 (<c>Density</c>,
/// <c>FuelLowerHeating</c>, <c>H2Smass</c>, <c>MercaptanSulphurContent</c>) читає лише ECW_C05_05 «Boilers – Gas».
/// Рідке паливо (дизель, гас, бензин) не має складу HSE400 і в C05_05 не потрапляє: для нього ці клітинки «не
/// застосовується» - ПОРОЖНЬО без <c>#N/A</c>, подання не блокують (doc 96: LTD-A01/A02 бензин, A03/A05 дизель -
/// 16 помилок на місяць). Береться ТОЙ САМИЙ текст моста й ТІ САМІ довідники (паливо T3_00, словник потоків), що
/// згенеровані в моделі Land (<c>land-template.model.json</c>); склад - синтетичні числа.
/// </summary>
/// <remarks>
/// Межа фіксу (✎ B98(3), F13 AU): порожнім стає лише паливо з кодами liquidFuels (у виразі - їхні назви: діалект
/// шаблонів коду запису не читає). Газ зі складом дає значення; газ БЕЗ запису складу за місяць і газ, чий запис
/// словника потоків видалено, - <c>#N/A</c> (справжня прогалина даних, не тиха порожнеча); помилка самого Lookup палива
/// (невидимий запис - <c>#REF</c>) теж не ховається.
/// Доказ B98(3): на моделі з IFERROR навколо REGFIND словника потоків (d8d6665e3) червоніли
/// <see cref="Газ_без_запису_у_словнику_потоків_дає_NA"/> ×2 і <see cref="Невідоме_паливо_поза_словником_потоків_дає_NA"/>
/// (null замість #N/A). Мутаційні докази B89 (модель 13.11) - історія коміту d8d6665e3.
/// </remarks>
public sealed class StationaryFuelHse400BridgesTests
{
    private const string FuelRegistry = "LAND_STATIONARY_TYPE_OF_FUEL";

    private static readonly string[] Bridges = ["Density", "FuelLowerHeating", "H2Smass", "MercaptanSulphurContent"];

    private static readonly Dictionary<string, string> IndicatorOf = new()
    {
        ["Density"] = "Density20",
        ["FuelLowerHeating"] = "FuelLowerHeating20",
        ["H2Smass"] = "H2S_mass",
        ["MercaptanSulphurContent"] = "MercaptanSulphurContent",
    };

    public static TheoryData<string, string> LiquidRows()
    {
        var data = new TheoryData<string, string>();
        foreach (var fuel in new[] { "DIESEL", "GASOLINE", "KEROSENEJETFUEL" })
        {
            foreach (var column in Bridges)
            {
                data.Add(fuel, column);
            }
        }

        return data;
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [MemberData(nameof(LiquidRows))]
    public void Рідке_паливо_дає_порожньо_без_помилки(string fuel, string column)
    {
        var (snapshot, ids) = Arrange();

        var value = Eval(Bridge(column), snapshot, ExpressionValue.Number(ids[fuel]));

        Assert.False(value.IsError, $"T3_00 {fuel} {column}: {value.ErrorCode}");
        Assert.True(value.IsNull, $"T3_00 {fuel} {column}: {value.Value}");
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("FUELGASHMB", "FG_HMB")]
    [InlineData("MAKATGAS", "FG_Makat")]
    public void Газ_зі_складом_дає_значення_складу(string fuel, string stream)
    {
        var (snapshot, ids) = Arrange();

        foreach (var column in Bridges)
        {
            var value = Eval(Bridge(column), snapshot, ExpressionValue.Number(ids[fuel]));
            Assert.Equal(Composition(stream, IndicatorOf[column]), value.AsNumber());
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Газ_без_запису_складу_лишається_NA()
    {
        var (snapshot, ids) = Arrange();

        foreach (var column in Bridges)
        {
            // LPG (HMB) - потік HSE400, але складу за лютий немає: C05_05 не рахує, прогалина даних видна.
            Assert.Equal(
                ExpressionErrors.NotAvailable,
                Eval(Bridge(column), snapshot, ExpressionValue.Number(ids["LPGHMB"])).ErrorCode);

            // Fuel gas (HMB) за квітень - запису складу немає.
            Assert.Equal(
                ExpressionErrors.NotAvailable,
                Eval(Bridge(column), snapshot, ExpressionValue.Number(ids["FUELGASHMB"]), month: 4).ErrorCode);
        }
    }

    /// <summary>
    /// B98(3), F13 AU: адміністратор видалив запис газового потоку зі словника LAND_HSE400_STREAM - рядок газу мав би
    /// показати прогалину (#N/A), а не стати тихо порожнім, як рідке паливо.
    /// </summary>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("FUELGASHMB")]
    [InlineData("MAKATGAS")]
    public void Газ_без_запису_у_словнику_потоків_дає_NA(string fuel)
    {
        var (snapshot, ids) = Arrange(dropStreamOf: fuel);

        foreach (var column in Bridges)
        {
            // ⛔ RED до B98(3): IFERROR навколо REGFIND словника потоків давав NULL (порожньо) і для газу.
            Assert.Equal(
                ExpressionErrors.NotAvailable,
                Eval(Bridge(column), snapshot, ExpressionValue.Number(ids[fuel])).ErrorCode);
        }
    }

    /// <summary>
    /// B98(3): «рідке» визначається кодом запису (DIESEL/GASOLINE/KEROSENEJETFUEL), який генератор перетворює на назву
    /// (діалект шаблонів коду запису не читає - REGFIELD бачить лише поля, у довідника палива поле одне - NAME).
    /// Перейменований запис рідкого палива не стає «газом без складу» тихо: він видно дає #N/A (а не порожньо).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Невідоме_паливо_поза_словником_потоків_дає_NA()
    {
        var (snapshot, ids) = Arrange();

        foreach (var column in Bridges)
        {
            Assert.Equal(
                ExpressionErrors.NotAvailable,
                Eval(Bridge(column), snapshot, ExpressionValue.Number(ids["RENAMED"])).ErrorCode);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Порожнє_паливо_порожньо()
    {
        var (snapshot, _) = Arrange();

        foreach (var column in Bridges)
        {
            Assert.True(Eval(Bridge(column), snapshot, ExpressionValue.Null).IsNull, column);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Помилка_Lookup_палива_не_ховається()
    {
        var (snapshot, ids) = Arrange();

        foreach (var column in Bridges)
        {
            // Невидимий запис довідника палива: REGFIELD(…, 'NAME') дає #REF - це не «не потік HSE400».
            Assert.Equal(
                ExpressionErrors.BadReference,
                Eval(Bridge(column), snapshot, ExpressionValue.Number(ids["HIDDEN"])).ErrorCode);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Поза_словником_потоків_рівно_рідке_паливо_списку_T3_00()
    {
        // Список палива T3_00 мусить мати поза словником потоків рівно рідке паливо: новий газ без потоку в моделі дав би
        // #N/A на кожному рядку (з B98(3) - не тиха порожнеча) - цей тест і генератор зупиняють модель раніше.
        // Ключ словника потоків - без регістру (ignoreCase): «Fuel gas (Internal)» списку = «Fuel Gas (Internal)» словника.
        // Знімок прода шукає за канонічним ключем RegistryKeyNormalizer, тому членство звіряється саме ним.
        string? Canon(string s) => RegistryKeyNormalizer.Canonical([new RegistryKeyPart(CellDataType.String, s)]);

        using var model = LoadModel();
        var registries = model.RootElement.GetProperty("registries").EnumerateArray()
            .ToDictionary(r => r.GetProperty("code").GetString()!, r => r);
        var streamKey = registries["LAND_HSE400_STREAM"];
        Assert.True(Assert.Single(streamKey.GetProperty("keys").EnumerateArray()).GetProperty("ignoreCase").GetBoolean());
        var streams = streamKey.GetProperty("entries").EnumerateArray()
            .Select(e => Canon(e.GetProperty("fields").GetProperty("TEXT").GetString()!))
            .ToHashSet();
        var missing = registries[FuelRegistry].GetProperty("entries").EnumerateArray()
            .Where(e => !streams.Contains(Canon(e.GetProperty("name").GetString()!)))
            .Select(e => e.GetProperty("code").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["DIESEL", "GASOLINE", "KEROSENEJETFUEL"], missing);
    }

    private static decimal Composition(string stream, string indicator)
        => (stream == "FG_Makat" ? 10m : 20m) + Array.IndexOf(IndicatorOf.Values.ToArray(), indicator) + 0.5m;

    private static ExpressionValue Eval(string expression, InMemoryRegistrySnapshot snapshot, ExpressionValue fuel, int month = 2)
    {
        var inner = new TestEvaluationContext
        {
            Period = new PeriodContext(new DateOnly(2026, month, 1), new DateOnly(2026, month, 28), CalendarMode.Actual, 2026, (byte)month),
        };
        inner.SetCell("S", "T", string.Empty, "Land_TypeFuel", fuel);
        return Expr.Eval(expression, snapshot.Attach(inner), ExpressionDialect.Template);
    }

    private static JsonDocument LoadModel()
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "docs", "delivery", "reference-data", "land-template", "land-template.model.json")));

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Ecr.sln")))
        {
            root = root.Parent;
        }

        return root?.FullName ?? throw new InvalidOperationException("Не знайдено Ecr.sln від каталогу збірки вгору.");
    }

    private static string Bridge(string column)
    {
        using var model = LoadModel();
        var t = model.RootElement.GetProperty("sheets").EnumerateArray()
            .SelectMany(s => s.GetProperty("tables").EnumerateArray())
            .First(x => x.GetProperty("code").GetString() == "T3_00");
        return t.GetProperty("columns").EnumerateArray()
            .First(c => c.GetProperty("code").GetString() == column).GetProperty("expression").GetString()!;
    }

    /// <summary>
    /// Словник потоків і список палива T3_00 - з моделі Land; склад - лютий 2026, FG_HMB і FG_Makat.
    /// <paramref name="dropStreamOf"/> - код палива, чий запис словника потоків (за назвою, без регістру) «видалено».
    /// </summary>
    private static (InMemoryRegistrySnapshot Snapshot, Dictionary<string, long> Ids) Arrange(string? dropStreamOf = null)
    {
        var snapshot = new InMemoryRegistrySnapshot()
            .AddRegistry("LAND_HSE400_STREAM", ["TEXT", "PREFIX"], primaryKey: ["TEXT"])
            .AddRegistry("LAND_HSE400_COMPOSITION", ["YEAR", "MONTH", "STREAM", "INDICATOR", "VALUE"], primaryKey: ["YEAR", "MONTH", "STREAM", "INDICATOR"])
            .AddRegistry(FuelRegistry, ["NAME"]);
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);

        using var model = LoadModel();
        var dropText = dropStreamOf is null
            ? null
            : model.RootElement.GetProperty("registries").EnumerateArray()
                .Single(r => r.GetProperty("code").GetString() == FuelRegistry)
                .GetProperty("entries").EnumerateArray()
                .Single(e => e.GetProperty("code").GetString() == dropStreamOf)
                .GetProperty("name").GetString();
        long id = 100;
        foreach (var registry in model.RootElement.GetProperty("registries").EnumerateArray())
        {
            var code = registry.GetProperty("code").GetString()!;
            foreach (var entry in registry.GetProperty("entries").EnumerateArray())
            {
                var entryCode = entry.GetProperty("code").GetString()!;
                if (code == "LAND_HSE400_STREAM")
                {
                    if (string.Equals(entry.GetProperty("fields").GetProperty("TEXT").GetString(), dropText, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var values = entry.GetProperty("fields").EnumerateObject()
                        .ToDictionary(f => f.Name, f => ExpressionValue.Text(f.Value.GetString()!));
                    snapshot.AddEntry(code, ++id, entryCode, values, ordinal: (int)id);
                }
                else if (code == FuelRegistry)
                {
                    snapshot.AddEntry(
                        code, ++id, entryCode, new() { ["NAME"] = ExpressionValue.Text(entry.GetProperty("name").GetString()!) }, ordinal: (int)id);
                    ids[entryCode] = id;
                }
            }
        }

        Assert.Equal(8, ids.Count);
        snapshot.AddEntry(FuelRegistry, ++id, "HIDDEN", new() { ["NAME"] = ExpressionValue.Text("Diesel - Дизель") }, ordinal: (int)id, visible: false);
        ids["HIDDEN"] = id;
        snapshot.AddEntry(FuelRegistry, ++id, "RENAMED", new() { ["NAME"] = ExpressionValue.Text("Diesel (renamed)") }, ordinal: (int)id);
        ids["RENAMED"] = id;

        foreach (var stream in new[] { "FG_HMB", "FG_Makat" })
        {
            foreach (var indicator in IndicatorOf.Values)
            {
                snapshot.AddEntry(
                    "LAND_HSE400_COMPOSITION",
                    ++id,
                    $"H{id}",
                    new()
                    {
                        ["YEAR"] = ExpressionValue.Number(2026),
                        ["MONTH"] = ExpressionValue.Number(2),
                        ["STREAM"] = ExpressionValue.Text(stream),
                        ["INDICATOR"] = ExpressionValue.Text(indicator),
                        ["VALUE"] = ExpressionValue.Number(Composition(stream, indicator)),
                    },
                    ordinal: (int)id);
            }
        }

        return (snapshot, ids);
    }
}
