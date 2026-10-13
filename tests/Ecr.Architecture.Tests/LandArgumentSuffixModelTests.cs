using System.Text.Json;
using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// K1 (D-8, клас K - конфлікт джерел аргументів): одне ім'я колонки не може бути двома джерелами на одній таблиці.
/// Методології C11_01, C11_02 (OperatingTime) і C11_11_02 (SubstanceConsumption) читають власну колонку-міст
/// <c>&lt;аргумент&gt;__&lt;код методології&gt;</c>; решта лишається на незмінному імені. Мости будує
/// <c>af_attr_bridges.py</c> (<c>ARGUMENT_SUFFIXES</c>, розділ моделі <c>argumentBridges.argumentSuffixes</c>), токен у чернетці
/// підміняє етап Build <c>ArgumentSuffix</c>. Тест читає лише файли моделі.
/// </summary>
/// <remarks>
/// Мутаційний доказ (лише цей клас): у <c>ARGUMENT_SUFFIXES</c> прибрати ECW_C11_02 і перегенерувати - червоніють
/// <see cref="Суфіксовані_мости_є_в_кожній_таблиці_методології"/> і підрахунок 14; видалити звичайний міст
/// (наприклад T9_01.Land_Measure_SubstanceConsumption) - червоніє <see cref="Звичайні_мости_наявні_до_K1_не_видалені_і_не_змінені"/>.
/// </remarks>
public sealed class LandArgumentSuffixModelTests
{
    private static readonly string Dir = Path.Combine(
        SourceTree.Root, "docs", "delivery", "reference-data", "land-template");

    private static JsonDocument Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, name)));

    private static Dictionary<string, Dictionary<string, JsonElement>> Tables(JsonDocument model)
    {
        var res = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
        foreach (var t in model.RootElement.GetProperty("sheets").EnumerateArray()
                     .SelectMany(s => s.GetProperty("tables").EnumerateArray()))
        {
            var code = t.GetProperty("code").GetString()!;
            if (!res.ContainsKey(code))
            {
                res[code] = t.GetProperty("columns").EnumerateArray()
                    .ToDictionary(c => c.GetProperty("code").GetString()!, c => c, StringComparer.Ordinal);
            }
        }

        return res;
    }

    private static readonly string[] Methodologies = ["ECW_C11_01", "ECW_C11_02", "ECW_C11_11_02"];

    private static JsonElement Suffixes(JsonDocument model, string methodology)
        => model.RootElement.GetProperty("argumentBridges").GetProperty("argumentSuffixes")
            .GetProperty("methodologies").GetProperty(methodology);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Перелік_суфіксів_лише_три_методології_з_одним_аргументом_кожна()
    {
        using var model = Load("land-template.model.json");
        var all = model.RootElement.GetProperty("argumentBridges").GetProperty("argumentSuffixes").GetProperty("methodologies");

        Assert.Equal(Methodologies, all.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            ["Land_Measure_OperatingTime"],
            Suffixes(model, "ECW_C11_01").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(
            ["Land_Measure_OperatingTime"],
            Suffixes(model, "ECW_C11_02").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(
            ["Land_Measure_SubstanceConsumption"],
            Suffixes(model, "ECW_C11_11_02").EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Суфіксовані_мости_є_в_кожній_таблиці_методології()
    {
        using var model = Load("land-template.model.json");
        using var map = Load("land-result-outputs.json");
        var tables = Tables(model);
        var total = 0;

        foreach (var methodology in Methodologies)
        {
            var onTables = map.RootElement.GetProperty("methodologies").GetProperty(methodology).GetProperty("tables")
                .EnumerateArray().Select(t => t.GetString()!).ToHashSet(StringComparer.Ordinal);
            foreach (var arg in Suffixes(model, methodology).EnumerateObject())
            {
                var column = arg.Value.GetProperty("column").GetString()!;
                Assert.Equal(arg.Name + "__" + methodology, column);
                Assert.Matches("^[A-Za-z][A-Za-z0-9_]{0,63}$", column);

                var listed = arg.Value.GetProperty("tables").EnumerateArray().Select(t => t.GetString()!).ToHashSet();
                Assert.NotEmpty(listed);
                foreach (var table in onTables.Where(tables.ContainsKey))
                {
                    var has = tables[table].ContainsKey(column);
                    Assert.Equal(listed.Contains(table), has);
                    if (has)
                    {
                        total++;
                        var c = tables[table][column];
                        Assert.Equal("Formula", c.GetProperty("dataType").GetString());
                        Assert.True(c.GetProperty("readOnly").GetBoolean());
                        Assert.Contains("AF attribute", c.GetProperty("ecrAdded").GetString(), StringComparison.Ordinal);
                        foreach (Match m in Regex.Matches(c.GetProperty("expression").GetString()!, @"\[([^\]]+)\]"))
                        {
                            var token = m.Groups[1].Value;
                            Assert.True(
                                token.StartsWith("Period", StringComparison.Ordinal) || tables[table].ContainsKey(token),
                                $"{table}.{column}: [{token}] не колонка таблиці.");
                        }
                    }
                }

                // Таблиця методології, де аргумент читається, але моста немає, мусить бути названа в unresolved.
                foreach (var table in listed)
                {
                    Assert.True(tables[table].ContainsKey(column), $"{table}: {column} у переліку, але колонки немає.");
                }
            }
        }

        // C11_01: 5 таблиць (T9_04, T9a_16, T9a_17, T9a_27, T9a_30); C11_02: 3 (T9_04, T9a_16, T9a_27);
        // C11_11_02: 6 (T9_01, T9_07, T9_08, T9a_16, T9a_17a, T9a_19).
        Assert.Equal(14, total);
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    // джерело - колонка, яку методологія брала ЗА СТАРИМ КОДОМ (RENAMES): дні перебування / дні роботи, Sm3
    [InlineData("T9a_17", "Land_Measure_OperatingTime__ECW_C11_01", "[MaterialResidenceTimeInProcessUnits_M01]")]
    [InlineData("T9a_27", "Land_Measure_OperatingTime__ECW_C11_01", "[OperatingTime_M01_M]")]
    [InlineData("T9a_27", "Land_Measure_OperatingTime__ECW_C11_02", "[OperatingTime_M01_M]")]
    [InlineData("T9_07", "Land_Measure_SubstanceConsumption__ECW_C11_11_02", "[SubstanceConsumption20Dgr1atm_M01]")]
    [InlineData("T9a_19", "Land_Measure_SubstanceConsumption__ECW_C11_11_02", "[AmountOfOilPumpedIntoTheTank_M01]")]
    // звичайний міст з'явився, бо конфлікт зник: атрибут OperatingTime (години)
    [InlineData("T9a_17", "Land_Measure_OperatingTime", "[OperatingTime_M01]")]
    [InlineData("T9a_27", "Land_Measure_OperatingTime", "[OperatingTime_M01]")]
    public void Міст_читає_джерело_із_старого_коду(string table, string column, string fragment)
    {
        using var model = Load("land-template.model.json");

        Assert.Contains(
            fragment, Tables(model)[table][column].GetProperty("expression").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [InlineData("T9_04", "Land_Measure_OperatingTime")]
    [InlineData("T9a_16", "Land_Measure_OperatingTime")]
    [InlineData("T9a_30", "Land_Measure_OperatingTime")]
    [InlineData("T9_01", "Land_Measure_SubstanceConsumption")]
    [InlineData("T9_08", "Land_Measure_SubstanceConsumption")]
    [InlineData("T9a_16", "Land_Measure_SubstanceConsumption")]
    [InlineData("T9a_17a", "Land_Measure_SubstanceConsumption")]
    [InlineData("T9a_19", "Land_Measure_SubstanceConsumption")]
    public void Звичайні_мости_наявні_до_K1_не_видалені_і_не_змінені(string table, string column)
    {
        using var model = Load("land-template.model.json");
        var tables = Tables(model);

        // Formula-колонка з комірками, що зникає з версії шаблону, - lostValues у VersionMigrationPlanner: лише додаємо.
        Assert.True(tables[table].TryGetValue(column, out var ordinary), $"{table}.{column} зник.");
        Assert.Equal("Formula", ordinary.GetProperty("dataType").GetString());

        // Суфіксований міст копіює вираз звичайного там, де в обох те саме джерело (T9_04, T9a_16, T9a_30, T9_01, ...).
        foreach (var methodology in Methodologies)
        {
            if (!Suffixes(model, methodology).TryGetProperty(column, out var arg))
            {
                continue;
            }

            var sfx = arg.GetProperty("column").GetString()!;
            if (tables[table].TryGetValue(sfx, out var suffixed))
            {
                Assert.Equal(
                    ordinary.GetProperty("expression").GetString(), suffixed.GetProperty("expression").GetString());
            }
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Конфлікти_класу_K_закриті_а_T9_07_SubstanceConsumption_лишається_необовязковим_для_C11_10()
    {
        using var model = Load("land-template.model.json");
        var bridges = model.RootElement.GetProperty("argumentBridges");
        var unresolved = bridges.GetProperty("unresolved");
        var tables = Tables(model);

        foreach (var (table, argument) in new[]
                 {
                     ("T9a_17", "Land_Measure_OperatingTime"), ("T9a_27", "Land_Measure_OperatingTime"),
                 })
        {
            Assert.False(unresolved.TryGetProperty(table, out var u) && u.TryGetProperty(argument, out _));
            Assert.True(tables[table].ContainsKey(argument));
        }

        // Жодного [конфлікт] на аргументах із переліку суфіксів не лишилось.
        foreach (var t in unresolved.EnumerateObject())
        {
            foreach (var a in t.Value.EnumerateObject())
            {
                Assert.False(
                    a.Name.StartsWith("Land_Measure_OperatingTime", StringComparison.Ordinal)
                    && a.Value.GetString()!.StartsWith("[конфлікт]", StringComparison.Ordinal),
                    $"{t.Name}.{a.Name} лишився конфліктом.");
            }
        }

        // 9_07: звичайної колонки немає, C11_10 - у переліку необов'язкових (маркер ? у чернетці).
        Assert.False(tables["T9_07"].ContainsKey("Land_Measure_SubstanceConsumption"));
        Assert.Contains(
            "T9_07",
            bridges.GetProperty("optionalArguments").GetProperty("methodologies").GetProperty("ECW_C11_10")
                .GetProperty("tables").GetProperty("Land_Measure_SubstanceConsumption").EnumerateArray()
                .Select(t => t.GetString()));
    }
}
