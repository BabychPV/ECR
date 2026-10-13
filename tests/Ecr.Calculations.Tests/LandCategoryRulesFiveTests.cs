// tests/Ecr.Calculations.Tests/LandCategoryRulesFiveTests.cs
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// LP-07: правила категорії п'яти методологій Land, що мовчали (C11_01, C11_02, C12_01, C12_04_01, C12_04_02),
/// з файлу <c>af-land-category-rules.json</c>. Ключ складають з форми B: правило дає префікс, рушій дописує
/// код речовини ітерації. Кожен випадок — старий C#-код дає ту саму категорію, а різні групи — різні константи.
/// </summary>
/// <remarks>
/// Мутаційні докази: змінити токен потоку/групу у правилі — червоний відповідний випадок; прибрати кирилічний
/// варіант InvNum — червоний <see cref="C12_04_01_кирилічні_Т_і_А_дають_латинський_ключ"/>.
/// </remarks>
public sealed class LandCategoryRulesFiveTests
{
    private const string RulesFile = "docs/delivery/reference-data/land-template/af-land-category-rules.json";

    private const string Fuel = "Land_TypeFuelOrStream";
    private const string Nae = "Land_NAE";

    private const string FgInternal = "Fuel Gas (Internal) - Топливный газ (Внутренний)";
    private const string FgHmb = "Fuel gas (HMB) - Топливный газ (МТБ)";
    private const string LpgHmb = "LPG (HMB) - СУГ (МТБ)";
    private const string Sludge = "1-204 sludge - 1-204 шлам";
    private const string Caustic = "spent caustic - отработанный каустик";

    private static MethodologyConstant C(string category, decimal value) => RuleStand.Number("EF", value, category);

    private static RuleStand Stand(string methodology, string? codeA, string? codeB, params MethodologyConstant[] constants)
        => new RuleStand(LoadRules()[methodology])
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithSubstanceCodes(codeA, codeB)
            .WithConstants(constants);

    private static decimal? Total(CalculationOutput output, long substanceId)
        => output.Values.SingleOrDefault(v => v.OutputCode == "Total" && v.SubstanceEntryId == substanceId)?.Value;

    private static async Task AssertRejectedAsync(RuleStand stand, params CalculationArgument[] arguments)
    {
        var error = await Assert.ThrowsAsync<DomainException>(() => stand.RunAsync(arguments));

        Assert.Equal("err.ECR-CALC-0422.categoryRuleFailed", error.Details?["messageKey"]);
    }

    // ---------------- C11_01 ----------------

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData(FgInternal, 2)]
    [InlineData(FgHmb, 2)]
    [InlineData(LpgHmb, 3)]
    public async Task C11_01_газ_і_СУГ_дають_ключ_NAE_FG_або_LPG_без_коду(string fuel, int expected)
    {
        // Старий код: {nae}_{FG|LPG} без коду речовини; форма B пробує {ключ}_{код}, а лишається на {ключ}.
        var stand = Stand("ECW_C11_01", "333", "2754", C("6646_FG", 2m), C("6646_LPG", 3m), C("6647_FG", 9m));

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In(Fuel, fuel), RuleStand.In(Nae, "6646"));

        Assert.Equal(expected, Total(output, RuleStand.SubstanceA));
        Assert.Equal(expected, Total(output, RuleStand.SubstanceB));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task C11_01_шлам_і_каустик_дають_ключ_NAE_потік_код_і_NAE_із_пробілом()
    {
        var stand = Stand(
            "ECW_C11_01", "333", "2754",
            C("6783(002)_sludge_333", 5m), C("6783(002)_sludge_2754", 7m),
            C("6783(002)_caustic_333", 11m), C("6783(002)_caustic_2754", 13m));

        var sludge = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In(Fuel, Sludge), RuleStand.In(Nae, "6783 (002)"));
        var caustic = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In(Fuel, Caustic), RuleStand.In(Nae, "6783 (002)"));

        Assert.Equal(5m, Total(sludge, RuleStand.SubstanceA));
        Assert.Equal(7m, Total(sludge, RuleStand.SubstanceB));
        Assert.Equal(11m, Total(caustic, RuleStand.SubstanceA));
        Assert.Equal(13m, Total(caustic, RuleStand.SubstanceB));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("14", 1)]
    [InlineData("401", 2)]
    [InlineData("Waste water", 3)]
    [InlineData("Diesel - Дизель", 4)]
    [InlineData("452", 5)]
    public async Task C11_01_потоки_14_401_стічні_дизель_452_дають_свій_ключ(string stream, int expected)
    {
        var stand = Stand(
            "ECW_C11_01", "333", null,
            C("6780(001)_14_333", 1m), C("6780(001)_401_333", 2m), C("6780(001)_Wwater_333", 3m),
            C("6780(001)_Diesel_333", 4m), C("6780(001)_452_333", 5m));

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In(Fuel, stream), RuleStand.In(Nae, "6780 (001)"));

        Assert.Equal(expected, Total(output, RuleStand.SubstanceA));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task C11_01_потік_поза_переліком_відмовляє_рядку()
    {
        var stand = Stand("ECW_C11_01", "333", null, C("6646_FG", 2m));

        await AssertRejectedAsync(stand, RuleStand.In("X", 1m), RuleStand.In(Fuel, "Unknown stream"), RuleStand.In(Nae, "6646"));
    }

    // ---------------- C11_02 ----------------

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("6641", 2)]
    [InlineData("6645", 2)]
    [InlineData("6789 (001)", 4)]
    [InlineData("6586(003)", 5)]
    [InlineData("6785", 7)]
    public async Task C11_02_утиліти_беруть_групу_NAE_або_сам_NAE(string nae, int expected)
    {
        var stand = Stand(
            "ECW_C11_02", "333", null,
            C("6640", 2m), C("6789", 4m), C("6586", 5m), C("6783", 6m), C("6785", 7m));

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In(Fuel, FgInternal), RuleStand.In(Nae, nae));

        Assert.Equal(expected, Total(output, RuleStand.SubstanceA));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task C11_02_шлам_і_каустик_групи_додають_потік_і_код_речовини()
    {
        var stand = Stand(
            "ECW_C11_02", "333", "2754",
            C("6783_sludge_333", 4m), C("6783_sludge_2754", 6m),
            C("6789_caustic_333", 8m), C("6789_caustic_2754", 9m));

        var sludge = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In(Fuel, Sludge), RuleStand.In(Nae, "6786(001)"));
        var caustic = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In(Fuel, Caustic), RuleStand.In(Nae, "6789 (001)"));

        Assert.Equal(4m, Total(sludge, RuleStand.SubstanceA));
        Assert.Equal(6m, Total(sludge, RuleStand.SubstanceB));
        Assert.Equal(8m, Total(caustic, RuleStand.SubstanceA));
        Assert.Equal(9m, Total(caustic, RuleStand.SubstanceB));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("6586 (002)", 12)]
    [InlineData("6644", 14)]
    public async Task C11_02_шлам_груп_6586_і_6640_бере_ключ_групи(string nae, int expected)
    {
        var stand = Stand(
            "ECW_C11_02", "333", null,
            C("6586_sludge_333", 12m), C("6640_sludge_333", 14m), C("6783_sludge_333", 4m));

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In(Fuel, Sludge), RuleStand.In(Nae, nae));

        Assert.Equal(expected, Total(output, RuleStand.SubstanceA));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task C11_02_шлам_для_NAE_поза_групами_лишається_на_самому_NAE_зі_збігом_без_пробілів()
    {
        // Старий код: Category = nae («6585(001)»), AF зберігає «6585 (001)» — збіг без пробілів (улюблений випадок LP-07).
        var stand = Stand("ECW_C11_02", "333", null, C("6585 (001)", 8m), C("6783_sludge_333", 4m));

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In(Fuel, Sludge), RuleStand.In(Nae, "6585(001)"));

        Assert.Equal(8m, Total(output, RuleStand.SubstanceA));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task C11_02_значення_поза_довідником_потоків_відмовляє_рядку()
    {
        var stand = Stand("ECW_C11_02", "333", null, C("6640", 2m));

        await AssertRejectedAsync(stand, RuleStand.In("X", 1m), RuleStand.In(Fuel, "Unknown stream"), RuleStand.In(Nae, "6641"));
    }

    // ---------------- C12_01 ----------------

    public static TheoryData<string, string> Streams() => new()
    {
        { "Fuel gas", "FuelGas" },
        { "1-D1", "1D-1" },
        { "1D-1", "1D-1" },
        { "Diesel/arctic diesel", "Diesel" },
        { "Diesel", "Diesel" },
        { "1D-114", "1D-114" },
        { "Lube oil", "LubeOil" },
        { "Hydraulic oil", "HydraulicOil" },
        { "Hydraulic Oil", "HydraulicOil" },
        { "Seal oil", "SealOil" },
        { "TEGlycol", "TEGlycol" },
        { "Antifoam", "Antifoam" },
        { "Corrosion inhibitor", "CorrosionInhibitor" },
        { "Methanol/Метанол", "Methanol" },
        { "Scale inhibitor", "ScaleInhibitor" },
        { "TEGAntifoam", "TEGAntifoam" },
        { "TEG pH Adjuster/", "TEGpHAdjuster" },
        { "Wax inhibitor", "WaxInhibitor" },
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [MemberData(nameof(Streams))]
    public async Task C12_01_потік_дає_ключ_типу_потоку_і_код_речовини(string stream, string token)
    {
        var tokens = Streams().Select(row => (string)((object[])row)[1]).Distinct().Order().ToList();
        var constants = tokens
            .SelectMany((t, i) => new[] { C(t + "_333", 100 + i), C(t + "_2754", 200 + i) })
            .ToArray();
        var stand = Stand("ECW_C12_01", "333", "2754", constants);

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In("Land_Streams", stream));

        var index = tokens.IndexOf(token);
        Assert.Equal(100 + index, Total(output, RuleStand.SubstanceA));
        Assert.Equal(200 + index, Total(output, RuleStand.SubstanceB));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("Asphaltene inhibitor")]
    [InlineData("fuel gas")]
    [InlineData("Unknown stream")]
    public async Task C12_01_потік_поза_словником_відмовляє_рядку(string stream)
    {
        // Старий код: «Error typeOfStream» і continue (точний збіг, регістр значущий); Asphaltene — без переліку кодів.
        var stand = Stand("ECW_C12_01", "333", null, C("FuelGas_333", 1m));

        await AssertRejectedAsync(stand, RuleStand.In("X", 1m), RuleStand.In("Land_Streams", stream));
    }

    // ---------------- C12_04_01 ----------------

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("B4-120-TA-101", 1)]
    [InlineData("B4-120-TA-201", 2)]
    [InlineData("B4-380-VA-105", 3)]
    [InlineData("B4-120-TC-401", 4)]
    public async Task C12_04_01_латинський_InvNum_дає_свій_ключ(string invNum, int expected)
    {
        var stand = Stand(
            "ECW_C12_04_01", "1001", "1052",
            C("B4-120-TA-101_1001", 1m), C("B4-120-TA-201_1001", 2m), C("B4-380-VA-105_1001", 3m), C("B4-120-TC-401_1001", 4m));

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In("Land_InvNumEqp", invNum));

        Assert.Equal(expected, Total(output, RuleStand.SubstanceA));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("B4-120-ТA-101")] // кирилічна Т
    [InlineData("B4-120-TА-101")] // кирилічна А
    [InlineData("B4-120-ТА-101")] // обидві
    public async Task C12_04_01_кирилічні_Т_і_А_дають_латинський_ключ(string invNum)
    {
        var stand = Stand(
            "ECW_C12_04_01", "1001", "1052",
            C("B4-120-TA-101_1001", 1m), C("B4-120-TA-101_1052", 6m), C("B4-120-TA-201_1001", 2m));

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In("Land_InvNumEqp", invNum));

        Assert.Equal(1m, Total(output, RuleStand.SubstanceA));
        Assert.Equal(6m, Total(output, RuleStand.SubstanceB));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task C12_04_01_кирилічна_А_у_VA_теж_нормалізується()
    {
        var stand = Stand("ECW_C12_04_01", "1023", null, C("B4-380-VA-205_1023", 3m));

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In("Land_InvNumEqp", "B4-380-VА-205"));

        Assert.Equal(3m, Total(output, RuleStand.SubstanceA));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("B4-450-TC-001")]
    [InlineData("b4-120-ta-101")]
    [InlineData("")]
    public async Task C12_04_01_невідомий_InvNum_відмовляє_рядку(string invNum)
    {
        // Старий код: «Error invNum» і continue; регістр значущий, тож нижній регістр — теж відмова.
        var stand = Stand("ECW_C12_04_01", "1001", null, C("B4-120-TA-101_1001", 1m));

        await AssertRejectedAsync(stand, RuleStand.In("X", 1m), RuleStand.In("Land_InvNumEqp", invNum));
    }

    // ---------------- C12_04_02 ----------------

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("Diesel - Дизель", 2, 3)]
    [InlineData("Diesel - Дизельное топливо", 2, 3)]
    public async Task C12_04_02_дизель_дає_ключ_diesel_з_кодами_333_і_2754(string material, int a, int b)
    {
        var stand = Stand(
            "ECW_C12_04_02", "333", "2754",
            C("diesel_333", 2m), C("diesel_2754", 3m), C("oil_2735", 4m));

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In("Land_TypeStoredMaterial", material));

        Assert.Equal(a, Total(output, RuleStand.SubstanceA));
        Assert.Equal(b, Total(output, RuleStand.SubstanceB));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("Hydraulic oil - Гидравлическая жидкость")]
    [InlineData("Corrosion inhibitor - Ингибитор коррозии")]
    [InlineData("")]
    public async Task C12_04_02_решта_матеріалів_дають_ключ_oil(string material)
    {
        var stand = Stand(
            "ECW_C12_04_02", "333", "2735",
            C("diesel_333", 2m), C("diesel_2754", 3m), C("oil_2735", 4m));

        var output = await stand.RunAsync(RuleStand.In("X", 1m), RuleStand.In("Land_TypeStoredMaterial", material));

        Assert.Equal(4m, Total(output, RuleStand.SubstanceB)); // oil_2735
        Assert.Null(Total(output, RuleStand.SubstanceA)); // немає oil_333 — #REF, не чужа константа
    }

    // ---------------- файл правил ----------------

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Файл_правил_містить_усі_п_ять_методологій_у_межах_довжини()
    {
        var rules = LoadRules();

        foreach (var name in new[] { "ECW_C11_01", "ECW_C11_02", "ECW_C12_01", "ECW_C12_04_01", "ECW_C12_04_02" })
        {
            Assert.True(rules.ContainsKey(name), name);
            Assert.InRange(rules[name].Length, 1, MethodologyFormula.MaxExpressionLength);
        }
    }

    private static Dictionary<string, string> LoadRules()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, RulesFile)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, RulesFile)));

        return doc.RootElement.GetProperty("rules").EnumerateArray().ToDictionary(
            r => r.GetProperty("methodology").GetString()!,
            r => r.GetProperty("expression").GetString()!,
            StringComparer.Ordinal);
    }
}
