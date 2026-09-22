using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Класифікація рядка правилами — спільна для прогону й матриці покриття (ФВ-13.4, ФВ-13.9).
/// </summary>
public sealed class MethodologyRuleClassifyTests
{
    private static readonly Dictionary<string, string?> Co2Flare = new(StringComparer.Ordinal)
    {
        ["11"] = "CO2",
        ["12"] = "Flare",
    };

    private static IReadOnlyList<CompiledMethodologyRule> Rules(params (string Code, string Json, int Priority)[] rules)
        => MethodologyRuleMatcher.Compile(rules
            .OrderBy(r => r.Priority)
            .Select(r => new MethodologyRule(1, EcrCode.Create(r.Code), r.Json, r.Priority)));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.9")]
    public void Жодне_правило_не_збіглося_це_розрив()
    {
        var result = MethodologyRuleMatcher.Classify(Rules(("SO2", """{"11":"SO2"}""", 10)), Co2Flare);

        Assert.Null(result.Winner);
        Assert.Empty(result.Matches);
        Assert.False(result.IsTie);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.9")]
    public void Два_збіги_з_рівним_пріоритетом_це_нічия_переможець_перший()
    {
        var result = MethodologyRuleMatcher.Classify(
            Rules(("A", """{"11":"CO2"}""", 10), ("B", """{"12":"Flare"}""", 10)), Co2Flare);

        Assert.Equal("A", result.Winner?.Code);
        Assert.True(result.IsTie);
        Assert.Equal(["A", "B"], result.Matches.Select(m => m.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-13.4")]
    public void Нижчий_пріоритет_лише_затінений_а_порожнє_правило_це_запасне()
    {
        var shadowed = MethodologyRuleMatcher.Classify(
            Rules(("EXACT", """{"11":"CO2"}""", 10), ("ALL", "{}", 100)), Co2Flare);

        Assert.Equal("EXACT", shadowed.Winner?.Code);
        Assert.False(shadowed.IsTie);
        Assert.Equal(2, shadowed.Matches.Count);

        var fallback = MethodologyRuleMatcher.Classify(
            Rules(("EXACT", """{"11":"NOx"}""", 10), ("ALL", "{}", 100)), Co2Flare);

        Assert.Equal("ALL", fallback.Winner?.Code);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Битий_json_не_збігається_ні_з_чим()
    {
        var result = MethodologyRuleMatcher.Classify(
            Rules(("BROKEN", "{ не json", 1), ("ARRAY", "[]", 2)), Co2Flare);

        Assert.Null(result.Winner);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Текст_значення_однаковий_для_прогону_і_матриці()
    {
        Assert.Equal("12.50", MethodologyRuleMatcher.Text(new CellValueData { ValueNumeric = 12.50m }));
        Assert.Equal("7", MethodologyRuleMatcher.Text(new CellValueData { ValueRegistryEntryId = 7 }));
        Assert.Equal("True", MethodologyRuleMatcher.Text(new CellValueData { ValueBool = true }));
        Assert.Null(MethodologyRuleMatcher.Text(CellValueData.Empty));
    }
}
