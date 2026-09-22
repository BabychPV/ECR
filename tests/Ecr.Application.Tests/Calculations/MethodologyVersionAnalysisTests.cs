// tests/Ecr.Application.Tests/Calculations/MethodologyVersionAnalysisTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Calculations.Dto;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>Матриця покриття і різниця версій методології (<c>BE-25</c>) — чиста логіка.</summary>
public sealed class MethodologyVersionAnalysisTests
{
    private const int Unit = 3;

    // ── Покриття ────────────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Покриття_кладе_активні_прив_язки_під_свій_вихід_і_називає_колонку_що_чекає()
    {
        var outputs = new[] { Output("tons", 1), Output("gsec", 2) };

        // Код прив'язки іншим регістром — той самий вихід: так їх зіставляє СУБД.
        var bindings = new[]
        {
            Binding(column: 11, "TONS"),
            Binding(column: 12, "tons"),
            Binding(column: 13, "gsec", active: false),
            Binding(column: 14, "ghost"),
        };

        var coverage = MethodologyCoverageHandler.Build(5, outputs, bindings);

        Assert.Equal(["tons", "gsec"], coverage.Outputs.Select(o => o.Code));
        Assert.Equal([11, 12], coverage.Outputs[0].Bindings.Select(b => b.ColumnDefId));

        // Вимкнена прив'язка не покриває: у прогоні її немає.
        Assert.Empty(coverage.Outputs[1].Bindings);
        Assert.Equal([14], coverage.WaitingBindings.Select(b => b.ColumnDefId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Без_прив_язок_кожен_вихід_не_лягає_нікуди_і_ніхто_не_чекає()
    {
        var coverage = MethodologyCoverageHandler.Build(5, [Output("tons", 1)], []);

        Assert.Empty(Assert.Single(coverage.Outputs).Bindings);
        Assert.Empty(coverage.WaitingBindings);
    }

    // ── Різниця версій ──────────────────────────────────────────────────────

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Різниця_знаходить_додане_і_прибране_в_кожному_наборі()
    {
        var before = Content(
            [Formula("old_f", "1")], [Constant("k1", 1m, from: null)], [Test("t_old", "{}")]);
        var after = Content(
            [Formula("new_f", "2")], [Constant("k1", 1m, from: null), Constant("k1", 2m, new DateOnly(2025, 1, 1))],
            [Test("t_new", "{}")]);

        var items = CompareMethodologyVersionsHandler.Compare(before, after);

        Assert.Equal(
            [
                (MethodologyDiffItemKind.Formula, "new_f", MethodologyDiffChange.Added),
                (MethodologyDiffItemKind.Formula, "old_f", MethodologyDiffChange.Removed),
                (MethodologyDiffItemKind.Constant, "k1", MethodologyDiffChange.Added),
                (MethodologyDiffItemKind.TestCase, "t_new", MethodologyDiffChange.Added),
                (MethodologyDiffItemKind.TestCase, "t_old", MethodologyDiffChange.Removed),
            ],
            items.Select(i => (i.Kind, i.Code, i.Change)));

        // Новий варіант константи з іншою датою — окремий запис, а не «змінене» k1.
        Assert.Equal(new DateOnly(2025, 1, 1), items[2].ValidFrom);
        Assert.Equal("2", items[2].After);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Різниця_бачить_змінений_вираз_формули_і_мовчить_про_незмінене()
    {
        var before = Content([Formula("e_co2", "@Fuel * CST.k"), Formula("same", "1")], [], []);
        var after = Content([Formula("E_CO2", "@Fuel * CST.k * 2"), Formula("same", "1")], [], []);

        var item = Assert.Single(CompareMethodologyVersionsHandler.Compare(before, after));

        Assert.Equal(MethodologyDiffChange.Changed, item.Change);
        Assert.Equal(["expression"], item.ChangedFields);
        Assert.Equal("@Fuel * CST.k", item.Before);
        Assert.Equal("@Fuel * CST.k * 2", item.After);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    public void Різниця_бачить_змінене_значення_константи_і_допуск_тесту()
    {
        var before = Content([], [Constant("gwp", 25m, null)], [Test("t1", """{"x":1}""", 0.01m)]);
        var after = Content([], [Constant("gwp", 28m, null)], [Test("t1", """{"x":1}""", 0.1m)]);

        var items = CompareMethodologyVersionsHandler.Compare(before, after);

        Assert.Equal(["value"], items[0].ChangedFields);
        Assert.Equal(("25", "28"), (items[0].Before, items[0].After));
        Assert.Equal(["tolerance"], items[1].ChangedFields);
    }

    private static MethodologyOutput Output(string code, int ordinal) => new(5, EcrCode.Create(code), Unit, ordinal);

    private static CalculationBinding Binding(int column, string output, bool active = true)
    {
        var binding = new CalculationBinding(tableDefId: 1, column, methodologyId: 2, output, "{}");
        binding.Update("{}", active);
        return binding;
    }

    private static VersionContent Content(
        MethodologyFormula[] formulas, MethodologyConstant[] constants, MethodologyTestCaseEntity[] tests)
        => new(formulas, constants, tests);

    private static MethodologyFormula Formula(string code, string expression) => new(5, EcrCode.Create(code), expression);

    private static MethodologyConstant Constant(string code, decimal value, DateOnly? from)
    {
        var constant = new MethodologyConstant(5, EcrCode.Create(code), value, Unit);
        constant.SetValidity(from, null);
        return constant;
    }

    private static MethodologyTestCaseEntity Test(string code, string expected, decimal tolerance = 0m)
        => new(5, code, "{}", expected, tolerance);
}
