// tests/Ecr.Calculations.Tests/LandC0707CategoryRuleTests.cs
using System.Text.Json;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// LAND-5094, діагноз Е: правило категорії ECW_C07_07 (файл <c>af-land-category-rules.json</c>) для кожного запису
/// довідника LAND_OTHER_TANKFARM дає ту саму категорію, що старий <c>ECW_C07_07.cs:94-144</c>. До правки правило
/// v8 не мало гілок Kerosene і Fuel: записи 'Jet fuel/Avgas/Kerosene' і 'Solvent/Kerosene' давали порожній ключ,
/// константи категорії Kerosene не знаходились, tons_*/gsec_* були #REF.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати гілку Kerosene (або один із двох записів) у правилі — червоний
/// <see cref="Запис_довідника_дає_категорію_старого_коду"/> для Kerosene; прибрати гілку Fuel — червоний
/// випадок «Fuel oil»; поміняти Base на Diesel — червоний Bilge/Lubricant.
/// </remarks>
public sealed class LandC0707CategoryRuleTests
{
    private const string RulesFile = "docs/delivery/reference-data/land-template/af-land-category-rules.json";

    // Імена записів — дослівно з LAND_OTHER_TANKFARM (land-template.model.json) і з порівнянь старого коду.
    public static TheoryData<string, string> Cases() => new()
    {
        { "Bilge water - Трюмные (подсланевые) воды", "Base" },
        { "Lubricant/Mineral oil - Смазочные материалы/Mинеральные масла", "Base" },
        { "Diesel - Дизель", "Diesel" },
        { "Gasoline - Бензин", "Gasoline" },
        { "Recovered oil - Восстановленное масло", "Recovered" },
        { "Jet fuel/Avgas/Kerosene - Авиационное топливо/Керосин", "Kerosene" },
        { "Solvent/Kerosene - Растворитель/Керосин", "Kerosene" },
        { "Fuel oil - Топливная нефть", "Fuel" },
        { "Hydrogen chloride 15% - Водород хлористый 15%", "" },
        { "Невідоме значення", "" },
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [MemberData(nameof(Cases))]
    public void Запис_довідника_дає_категорію_старого_коду(string registryName, string expectedCategory)
    {
        const long EntryId = 57;
        var context = new TestEvaluationContext();
        context.Arguments["Land_TypeStorMaterial"] = ExpressionValue.Number(EntryId);
        context.SetRegistryField(EntryId, "NAME", ExpressionValue.Text(registryName));

        var value = Expr.Eval(LoadRule(), context, ExpressionDialect.Methodology);

        Assert.Equal(expectedCategory, value.Value);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Правило_читає_лише_аргумент_Land_TypeStorMaterial()
    {
        // categoryRuleUnknownArgument (R16-4): аргумент правила мусить бути колонкою прив'язок T5_08.
        var names = System.Text.RegularExpressions.Regex.Matches(LoadRule(), "@([A-Za-z_][A-Za-z0-9_]*)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();

        Assert.Equal(["Land_TypeStorMaterial"], names);
    }

    private static string LoadRule()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, RulesFile)))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, RulesFile)));

        return doc.RootElement.GetProperty("rules").EnumerateArray()
            .Single(r => r.GetProperty("methodology").GetString() == "ECW_C07_07")
            .GetProperty("expression").GetString()!;
    }
}
