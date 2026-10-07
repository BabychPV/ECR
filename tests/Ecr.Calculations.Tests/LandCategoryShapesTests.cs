// tests/Ecr.Calculations.Tests/LandCategoryShapesTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Форми категорій методологій Land (5.1, 5.2, 5.4, 6.1, 7.7, 7.8, 7.21) — мета-доказ L-2: кожна з них
/// раніше давала <c>ECR-CALC-0422 constantAmbiguous</c> на КОЖНОМУ рядку, тепер правило версії вибирає
/// одну константу.
/// </summary>
/// <remarks>
/// ⛔ Кількість і назви категорій — з прод-БД AF (<c>dbo.AF_Constants</c>, SELECT; дизайн
/// <c>DESIGN-category-rule.md</c> §2): 5.1 — 16 (Loc/EU × BeforeMR/AfterMR × A…D), 5.2 — Summer/Winter,
/// 5.4 — Diesel/Kerosene, 6.1 — Diesel/Gas/Gasoline, 7.7 — Base/Diesel/Fuel/Gasoline/Kerosene/Recovered,
/// 7.8 — Diesel/Gasoline/Kerosene/MineralOil, 7.21 — Summer/Winter + регіональні. Вирази правил —
/// діалект Methodology (<c>if</c>, <c>in</c>, `=`): <c>Contains</c>/<c>StartsWith</c>/<c>Concat</c> у ньому
/// немає, тож відображення «значення поля реєстру → ключ» пишеться рівністю чи <c>in</c>. Тексти значень
/// реєстру (<c>Diesel - Дизель</c>) — з AF; чи збігаються вони з тим, що віддає реєстр ECR, треба
/// перевірити на стенді.
///
/// Мутаційний доказ: рушій без передачі ключа в `ResolveConstants` (<c>category: null</c>) — червоні
/// всі випадки <see cref="Правило_вибирає_одну_з_багатьох_категорій_замість_constantAmbiguous"/>.
/// </remarks>
public sealed class LandCategoryShapesTests
{
    /// <summary>Форма методології: правило, Row-формули, категорії EF, спільні константи й аргументи рядка.</summary>
    private sealed record Shape(
        string Rule,
        (string Code, string Expression)[] RowFormulas,
        string[] Categories,
        MethodologyConstant[] Common,
        Func<string, CalculationArgument[]> Row);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData("5.1", "Loc_AfterMR_B", 6)]
    [InlineData("5.1", "EU_BeforeMR_D", 12)]
    [InlineData("5.2", "Summer", 1)]
    [InlineData("5.2", "Winter", 2)]
    [InlineData("5.4", "Kerosene", 2)]
    [InlineData("6.1", "Gasoline", 3)]
    [InlineData("6.1", "Gas", 2)]
    [InlineData("7.7", "Fuel", 3)]
    [InlineData("7.7", "Base", 1)]
    [InlineData("7.8", "MineralOil", 4)]
    [InlineData("7.21", "Winter", 2)]
    [InlineData("7.21", "Region3", 5)]
    public async Task Правило_вибирає_одну_з_багатьох_категорій_замість_constantAmbiguous(
        string methodology, string expectedKey, int expectedFactor)
    {
        var shape = Shapes[methodology];
        var stand = new RuleStand(shape.Rule);

        foreach (var (code, expression) in shape.RowFormulas)
        {
            stand.WithRowFormula(code, expression, FormulaResultType.Text);
        }

        // Коефіцієнт категорії = її номер за порядком у переліку AF (1…N).
        stand.WithSubstanceFormula("Total", "@X * CST.EF")
             .WithConstants([
                 .. shape.Categories.Select((category, index) => RuleStand.Number("EF", index + 1, category)),
                 .. shape.Common]);

        var output = await stand.RunAsync([RuleStand.In("X", 10m), .. shape.Row(expectedKey)]);

        // Обидві речовини беруть коефіцієнт обраної категорії: X × factor.
        Assert.Equal(
            [10m * expectedFactor, 10m * expectedFactor],
            output.Values.Where(v => v.OutputCode == "Total").Select(v => v.Value));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Ключ_якого_у_AF_немає_дає_REF_а_не_заповнювач_і_не_число_іншої_категорії()
    {
        var stand = new RuleStand("@Cat")
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants(RuleStand.Number("EF", 1m, "A"), RuleStand.Number("EF", 2m, "B"));

        var output = await stand.RunAsync(RuleStand.In("X", 10m), RuleStand.In("Cat", "C"));

        Assert.DoesNotContain(output.Values, v => v.OutputCode == "Total");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Без_правила_шістнадцять_категорій_лишаються_неоднозначними()
    {
        var stand = new RuleStand(rule: null)
            .WithSubstanceFormula("Total", "@X * CST.EF")
            .WithConstants([.. Sixteen().Select((category, index) => RuleStand.Number("EF", index + 1, category))]);

        var error = await Assert.ThrowsAsync<DomainException>(() => stand.RunAsync(RuleStand.In("X", 10m)));

        Assert.Equal("err.ECR-CALC-0422.constantAmbiguous", error.Details?["messageKey"]);
    }

    private static string[] Sixteen()
        =>
        [
            "Loc_BeforeMR_A", "Loc_BeforeMR_B", "Loc_BeforeMR_C", "Loc_BeforeMR_D",
            "Loc_AfterMR_A", "Loc_AfterMR_B", "Loc_AfterMR_C", "Loc_AfterMR_D",
            "EU_BeforeMR_A", "EU_BeforeMR_B", "EU_BeforeMR_C", "EU_BeforeMR_D",
            "EU_AfterMR_A", "EU_AfterMR_B", "EU_AfterMR_C", "EU_AfterMR_D",
        ];

    /// <summary>k1/k2 лежать у AF як спільні константи (Category = "Common").</summary>
    private static MethodologyConstant[] SeasonConstants()
        =>
        [
            RuleStand.Text("k1_Sel", "Summer", MethodologyConstant.CommonCategory),
            RuleStand.Text("k2_Sel", "Winter", MethodologyConstant.CommonCategory),
        ];

    private const string SeasonRow = "if(@Month >= 4 and @Month <= 9, CST.k1_Sel, CST.k2_Sel)";

    private static CalculationArgument[] Month(string key) => [RuleStand.In("Month", key == "Summer" ? 6m : 12m)];

    private static readonly Dictionary<string, Shape> Shapes = new(StringComparer.Ordinal)
    {
        // 5.1: ключ — готовий текст поля рядка. ECW_Location у AF складається з двох полів через `+`, а в
        // діалекті Methodology `+` на тексті не конкатенує (#VALUE) — це питання до імпорту формул, не до L-2.
        ["5.1"] = new(
            "@Location", [], Sixteen(), [RuleStand.Number("K5", 1m, MethodologyConstant.CommonCategory)],
            key => [RuleStand.In("Location", key)]),

        // 5.2: Summer/Winter за місяцем - Row-формула читає Common-константи k1/k2.
        ["5.2"] = new(
            "!ECW_Category", [("ECW_Category", SeasonRow)], ["Summer", "Winter"], SeasonConstants(), Month),

        // 7.21: сезон або регіон (останній - Region3).
        ["7.21"] = new(
            "if(@Region = 'R3', 'Region3', !ECW_Category)",
            [("ECW_Category", SeasonRow)],
            ["Summer", "Winter", "Region1", "Region2", "Region3", "Region4", "Region5"],
            SeasonConstants(),
            key => [RuleStand.In("Region", key == "Region3" ? "R3" : "R1"), .. Month(key)]),

        // 5.4: Diesel / Kerosene за полем палива.
        ["5.4"] = new(
            "if(@Land_TypeFuel = 'Diesel - Дизель', 'Diesel', 'Kerosene')",
            [],
            ["Diesel", "Kerosene"],
            [],
            key => [RuleStand.In("Land_TypeFuel", key == "Diesel" ? "Diesel - Дизель" : "Kerosene - Керосин")]),

        // 6.1: Diesel / Gas / Gasoline.
        ["6.1"] = new(
            "if(@Land_TypeFuel = 'Diesel - Дизель', 'Diesel', if(@Land_TypeFuel = 'Gasoline - Бензин', 'Gasoline', 'Gas'))",
            [],
            ["Diesel", "Gas", "Gasoline"],
            [],
            key => [RuleStand.In("Land_TypeFuel", key switch
            {
                "Diesel" => "Diesel - Дизель",
                "Gasoline" => "Gasoline - Бензин",
                _ => "Natural gas - Природный газ",
            })]),

        // 7.7: вісім значень матеріалу -> шість категорій.
        ["7.7"] = new(
            "if(in(@Land_TypeStorMater, 'Bilge water', 'Lubricant oil', 'Mineral oil'), 'Base', "
            + "if(in(@Land_TypeStorMater, 'Jet fuel', 'Solvent', 'Kerosene'), 'Kerosene', "
            + "if(@Land_TypeStorMater = 'Diesel', 'Diesel', if(@Land_TypeStorMater = 'Gasoline', 'Gasoline', "
            + "if(@Land_TypeStorMater = 'Recovered oil', 'Recovered', 'Fuel')))))",
            [],
            ["Base", "Diesel", "Fuel", "Gasoline", "Kerosene", "Recovered"],
            [],
            key => [RuleStand.In("Land_TypeStorMater", key switch
            {
                "Base" => "Bilge water",
                "Fuel" => "Fuel oil",
                _ => key,
            })]),

        // 7.8: чотири категорії за типом перекачуваної рідини.
        ["7.8"] = new(
            "if(@Land_TypePumpedLiquid = 'Diesel', 'Diesel', if(@Land_TypePumpedLiquid = 'Gasoline', 'Gasoline', "
            + "if(@Land_TypePumpedLiquid = 'Kerosene', 'Kerosene', 'MineralOil')))",
            [],
            ["Diesel", "Gasoline", "Kerosene", "MineralOil"],
            [],
            key => [RuleStand.In("Land_TypePumpedLiquid", key)]),
    };
}
