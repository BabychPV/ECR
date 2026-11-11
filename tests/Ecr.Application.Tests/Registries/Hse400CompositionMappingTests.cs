// tests/Ecr.Application.Tests/Registries/Hse400CompositionMappingTests.cs
using Ecr.Application.Registries;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// HSE400: відображення документа у рядки складу газу <c>LAND_HSE400_COMPOSITION</c>. Правила імен — ті самі, що в
/// імпорті зі старої бази (<c>tools/land/LandHse400Composition.Lib.ps1</c>); тут вони перевіряються по одному.
/// </summary>
/// <remarks>
/// Підміна рядків довідника в знімку на реальному SQL Server — <c>LandHse400CompositionRegistryTests</c>
/// (Infrastructure). Мутаційні докази (лише цей клас): прибрати <c>continue</c> у гілці «власна колонка _mass» —
/// червоніє <see cref="Груповий_показник_стає_масовим_лише_коли_власного_масового_рядка_немає"/>; поміняти суфікс
/// версії місцями — червоніє <see cref="Розрахунковий_рядок_із_тим_самим_іменем_що_рядок_вводу_дістає_суфікс_версії"/>.
/// </remarks>
public sealed class Hse400CompositionMappingTests
{
    private static Dictionary<string, IReadOnlyDictionary<string, string>> Raw(string stream, params string[] rowKeys)
        => new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [stream] = Hse400CompositionMapping.RawIndicators(stream, rowKeys),
        };

    private static Hse400DocumentCell Cell(string stream, string rowKey, decimal value, int month = 1)
        => new(2026, month, stream, rowKey, value);

    private static Dictionary<string, decimal> Rows(string stream, string[] rowKeys, params Hse400DocumentCell[] cells)
        => Hse400CompositionMapping.ToCompositionRows(cells, Raw(stream, rowKeys))
            .ToDictionary(r => r.Indicator, r => r.Value, StringComparer.Ordinal);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Розрахунковий_рядок_із_тим_самим_іменем_що_рядок_вводу_дістає_суфікс_версії()
    {
        var inner = Hse400CompositionMapping.RawIndicators("FG_Internal", ["H2S", "F_H2S", "F_Density_20"]);
        Assert.Equal("H2S", inner["H2S"]);
        Assert.Equal("H2S_V2", inner["F_H2S"]);
        Assert.Equal("Density_20", inner["F_Density_20"]);

        // Поза *_Internal стара база писала версію без підкреслення.
        var hmb = Hse400CompositionMapping.RawIndicators("FG_HMB", ["Nitrogen", "F_Nitrogen"]);
        Assert.Equal("NitrogenV2", hmb["F_Nitrogen"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Імена_показників_зводяться_до_канонічних_а_за_збігу_перемагає_перший_рядок()
    {
        var rows = Rows(
            "FG_Internal",
            ["Density_20", "Density20", "Vddg_0", "F_FuelLowerHeating_20"],
            Cell("FG_Internal", "Density_20", 0.7m),
            Cell("FG_Internal", "Density20", 9m),
            Cell("FG_Internal", "Vddg_0", 1.5m),
            Cell("FG_Internal", "F_FuelLowerHeating_20", 34.5m));

        Assert.Equal(0.7m, rows["Density20"]);
        Assert.Equal(1.5m, rows["Vddg0"]);
        Assert.Equal(34.5m, rows["FuelLowerHeating20"]);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Груповий_показник_стає_масовим_лише_коли_власного_масового_рядка_немає()
    {
        var alias = Rows("FG_Makat", ["C1_C5"], Cell("FG_Makat", "C1_C5", 3m));
        Assert.Equal(3m, alias["C1_C5_mass"]);
        Assert.False(alias.ContainsKey("C1_C5"));

        // Власний рядок «…_mass» є в таблиці, хоч і порожній: псевдонім не діє — за існуванням, не за значенням.
        var own = Rows("FG_Makat", ["C1_C5", "C1_C5_mass"], Cell("FG_Makat", "C1_C5", 3m));
        Assert.Empty(own);

        var both = Rows(
            "FG_Makat", ["C1_C5", "C1_C5_mass"], Cell("FG_Makat", "C1_C5", 3m), Cell("FG_Makat", "C1_C5_mass", 4m));
        Assert.Equal(4m, both["C1_C5_mass"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void У_потоках_HMB_етилмеркаптан_береться_з_другої_версії_а_без_неї_зникає()
    {
        string[] keys = ["EMercaptan_mass", "F_EMercaptanV2_mass"];

        var withV2 = Rows(
            "LPG_HMB", keys, Cell("LPG_HMB", "EMercaptan_mass", 1m), Cell("LPG_HMB", "F_EMercaptanV2_mass", 2m));
        Assert.Equal(2m, withV2["EMercaptan_mass"]);

        var withoutV2 = Rows("LPG_HMB", keys, Cell("LPG_HMB", "EMercaptan_mass", 1m));
        Assert.False(withoutV2.ContainsKey("EMercaptan_mass"));

        // Не HMB: значення лишається своїм.
        var other = Rows(
            "FG_Export", keys, Cell("FG_Export", "EMercaptan_mass", 1m), Cell("FG_Export", "F_EMercaptanV2_mass", 2m));
        Assert.Equal(1m, other["EMercaptan_mass"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Сірка_без_H2S_у_HMB_дублюється_в_меркаптанову_а_гексан_підставляється_за_групу_C6_C10()
    {
        string[] keys = ["TotalSulphurContentWithoutH2S", "C6_1_mass"];

        var hmb = Rows(
            "FG_HMB", keys, Cell("FG_HMB", "TotalSulphurContentWithoutH2S", 5m), Cell("FG_HMB", "C6_1_mass", 0.4m));
        Assert.Equal(5m, hmb["MercaptanSulphurContent"]);
        Assert.Equal(0.4m, hmb["C6_C10_mass"]);

        var other = Rows("FG_Makat", keys, Cell("FG_Makat", "TotalSulphurContentWithoutH2S", 5m));
        Assert.False(other.ContainsKey("MercaptanSulphurContent"));

        // Власне значення групи є — гексан його не перекриває.
        var own = Rows(
            "FG_Makat",
            ["C6_C10_mass", "C6_1_mass"],
            Cell("FG_Makat", "C6_C10_mass", 0.9m),
            Cell("FG_Makat", "C6_1_mass", 0.4m));
        Assert.Equal(0.9m, own["C6_C10_mass"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Місяці_і_потоки_не_змішуються_а_чужий_рядок_і_місяць_поза_роком_відкидаються()
    {
        var raw = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["FG_Internal"] = Hse400CompositionMapping.RawIndicators("FG_Internal", ["Density_20"]),
            ["LPG_HMB"] = Hse400CompositionMapping.RawIndicators("LPG_HMB", ["Density_20"]),
        };

        var rows = Hse400CompositionMapping.ToCompositionRows(
            [
                Cell("FG_Internal", "Density_20", 0.7m, month: 1),
                Cell("FG_Internal", "Density_20", 0.75m, month: 2),
                Cell("LPG_HMB", "Density_20", 1.9m, month: 1),
                Cell("FG_Internal", "Unknown", 5m, month: 1),
                Cell("FG_Internal", "Density_20", 5m, month: 13),
                Cell("FG_Export", "Density_20", 5m, month: 1),
            ],
            raw);

        Assert.Equal(3, rows.Count);
        Assert.Contains(new Hse400CompositionRow(2026, 1, "FG_Internal", "Density20", 0.7m), rows);
        Assert.Contains(new Hse400CompositionRow(2026, 2, "FG_Internal", "Density20", 0.75m), rows);
        Assert.Contains(new Hse400CompositionRow(2026, 1, "LPG_HMB", "Density20", 1.9m), rows);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Без_довідника_складу_чи_без_документів_джерело_знімка_лишається_тим_самим()
    {
        var empty = new RegistrySnapshotSource([], [], [], []);
        Hse400CompositionRow[] rows = [new(2026, 1, "FG_Internal", "Density20", 0.7m)];

        Assert.Same(empty, Hse400CompositionMapping.Overlay(empty, Hse400CompositionMapping.RegistryCode, rows, [2026]));
        Assert.Same(empty, Hse400CompositionMapping.Overlay(empty, Hse400CompositionMapping.RegistryCode, [], []));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Код_запису_як_в_імпорті_і_не_довший_за_64()
    {
        Assert.Equal(
            "H202603_FG_Internal_Density20",
            Hse400CompositionMapping.EntryCode(new Hse400CompositionRow(2026, 3, "FG_Internal", "Density20", 1m)));
        Assert.Equal(
            64,
            Hse400CompositionMapping.EntryCode(
                new Hse400CompositionRow(2026, 3, "LPG_Internal", new string('A', 80), 1m)).Length);
    }
}
