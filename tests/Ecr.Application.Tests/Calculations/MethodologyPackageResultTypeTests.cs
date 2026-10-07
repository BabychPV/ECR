// tests/Ecr.Application.Tests/Calculations/MethodologyPackageResultTypeTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Імпорт пакета: тип результату формули (<c>Text</c> із пакета, а не завжди Number) і пошук одиниці без огляду
/// на регістр. Мутація: повернути <c>FormulaResultType.Number</c> у планері → <see cref="Тип_Text_з_пакета_доходить_до_плану"/>
/// червоний; прибрати <c>OrdinalIgnoreCase</c>-пошук → <see cref="Одиниця_без_огляду_на_регістр_резолвиться"/> червоний.
/// </summary>
public sealed class MethodologyPackageResultTypeTests
{
    private static readonly TimeZoneInfo Atyrau = TimeZoneInfo.FindSystemTimeZoneById("Asia/Atyrau");

    private static readonly IReadOnlyDictionary<string, ExistingMethodology> Nothing =
        new Dictionary<string, ExistingMethodology>(StringComparer.OrdinalIgnoreCase);

    private static UnitCatalogSnapshot OrdinalUnits()
        => new(
            new Dictionary<string, UnitRef>(StringComparer.Ordinal)
            {
                ["one"] = new UnitRef(7, "one", 7),
                ["kg"] = new UnitRef(8, "kg", 1),
                ["kg_per_t"] = new UnitRef(21, "kg_per_t", 9),
            },
            new Dictionary<string, int>(StringComparer.Ordinal));

    private static MethodologyPackageDto Package(
        MethodologyPackageFormulaDto[] formulas, MethodologyPackageConstantDto[]? constants = null)
        => new(
            MethodologyPackagePlanner.FormatName, 1, "Common",
            [new MethodologyPackageMethodologyDto("M1", [new MethodologyPackageVersionDto("V1", formulas, constants ?? [])])],
            []);

    private static MethodologyPackageFormulaDto Formula(string name, string text, string? resultType)
        => new(name, "1", string.Empty, text, "2023-12-31T19:00:00Z", "9999-02-19T19:00:00Z", true, string.Empty, resultType);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Тип_Text_з_пакета_доходить_до_плану()
    {
        var plan = MethodologyPackagePlanner.Plan(
            Package([Formula("T", "'a' & 'b'", "Text"), Formula("N", "1", "Number"), Formula("D", "2", null), Formula("L", "'x'", " text ")]),
            Nothing, OrdinalUnits(), Atyrau);

        Assert.Empty(plan.Blockers);
        var formulas = plan.Methodologies.Single().Versions.Single().Content.Formulas.ToDictionary(f => f.Code, f => f.ResultType);
        Assert.Equal(FormulaResultType.Text, formulas["T"]);
        Assert.Equal(FormulaResultType.Number, formulas["N"]);
        Assert.Equal(FormulaResultType.Number, formulas["D"]);
        Assert.Equal(FormulaResultType.Text, formulas["L"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Різний_тип_результату_робить_вміст_різним()
    {
        var text = new ImportVersionContent([new ImportFormulaContent("F", "'a'", null, FormulaResultType.Text)], [], []);
        var number = new ImportVersionContent([new ImportFormulaContent("F", "'a'", null)], [], []);

        Assert.False(text.SameAs(number));
        Assert.True(text.SameAs(text));
    }

    [Theory]
    [InlineData("KG", 8)]
    [InlineData("Kg_Per_T", 21)]
    [InlineData("kg/t", 21)]
    [InlineData("kg / t", 21)]
    public void Одиниця_без_огляду_на_регістр_резолвиться(string unit, int expectedId)
    {
        var constant = new MethodologyPackageConstantDto(
            "K", string.Empty, unit, [new MethodologyPackageConstantValueDto(string.Empty, "1", "2.5", null, null)]);

        var plan = MethodologyPackagePlanner.Plan(Package([], [constant]), Nothing, OrdinalUnits(), Atyrau);

        Assert.Empty(plan.Blockers);
        Assert.DoesNotContain(plan.Warnings, w => w.Kind == "unitUnknown");
        Assert.Equal(expectedId, Assert.Single(plan.Methodologies.Single().Versions.Single().Content.Constants).UnitId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Невідома_одиниця_лишається_попередженням_unitUnknown()
    {
        var constant = new MethodologyPackageConstantDto(
            "K", string.Empty, "furlong", [new MethodologyPackageConstantValueDto(string.Empty, "1", "2.5", null, null)]);

        var plan = MethodologyPackagePlanner.Plan(Package([], [constant]), Nothing, OrdinalUnits(), Atyrau);

        Assert.Contains(plan.Warnings, w => w.Kind == "unitUnknown" && w.Subject == "K");
    }
}
