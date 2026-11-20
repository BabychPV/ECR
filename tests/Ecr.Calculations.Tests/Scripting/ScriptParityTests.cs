using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// П'ять пар «версія-формули ↔ еквівалентний скрипт» (MS T2.4): однакові вхідні рядки дають ОДНАКОВІ
/// <c>CalculationOutput.Values</c> у <c>Legacy</c> і <c>Strict</c>. Це доказ того, що скрипт не має власної
/// арифметики: числа йдуть тим самим <c>Evaluator</c>, а маскування й округлення — тими самими кроками.
/// </summary>
/// <remarks>
/// Мутаційні докази: прибрати `CalculationSteps.FinishStep` з <c>ScriptRowRun.Step</c> — червоніє
/// <see cref="Ділення_маскований_нуль_у_Legacy"/> (нуль не маскується); підмінити арифметику режиму в
/// <c>ScriptCalculationModule</c> — червоніють усі пари з <c>Legacy</c>.
/// </remarks>
public sealed class ScriptParityTests
{
    private sealed record Pair(
        string[] Codes, string[] Expressions, string Script, CalculationArgument[][] Rows);

    private static CalculationArgument N(string code, decimal value) => new(code, value, null, null);

    private static CalculationArgument S(string code, string value) => new(code, null, value, null);

    private static async Task<(IReadOnlyList<CalculationOutputValue> Formula, IReadOnlyList<CalculationOutputValue> Script)>
        RunBothAsync(Pair pair, NumericMode mode, CalculationArgument[] row)
    {
        const int version = ScriptStand.VersionId;
        var store = Substitute.For<IMethodologyStore>();
        var formulas = new List<MethodologyFormula>();
        var outputs = new List<MethodologyOutput>();
        for (var i = 0; i < pair.Codes.Length; i++)
        {
            var formula = new MethodologyFormula(version, EcrCode.Create(pair.Codes[i]), pair.Expressions[i]);
            formula.SetResultType(FormulaResultType.Number);
            formula.SetScope(MethodologyFormulaScope.Row);
            formula.SetEvaluationOrder(i + 1);
            formulas.Add(formula);

            // Проміжні формули ("T") без виходу: вихід лише для кодів з великої літери першої пари.
            if (pair.Script.Contains("output " + pair.Codes[i] + ":", StringComparison.Ordinal))
            {
                var output = new MethodologyOutput(version, EcrCode.Create(pair.Codes[i]), unitId: 1, ordinal: i + 1);
                output.SetPerSubstance(false);
                outputs.Add(output);
            }
        }

        store.GetFormulasAsync(version, Arg.Any<CancellationToken>()).Returns(formulas);
        store.GetOutputsAsync(version, Arg.Any<CancellationToken>()).Returns(outputs);
        store.GetSubstancesAsync(version, Arg.Any<CancellationToken>()).Returns([]);

        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodBoundsAsync(ScriptStand.DocumentId, ScriptStand.PeriodValue, Arg.Any<CancellationToken>())
               .Returns(new PeriodBounds(new DateOnly(2099, 1, 1), new DateOnly(2099, 1, 31)));
        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(new UnitCatalogSnapshot(
            new Dictionary<string, UnitRef>(StringComparer.OrdinalIgnoreCase) { ["kg"] = new(1, "kg", 1, 1m) },
            new Dictionary<string, int>(StringComparer.Ordinal)));

        var generic = new GenericCalculationModule(
            new RealFormulaEngine(),
            store,
            new ConstantResolver(Substitute.For<IConstantStore>()),
            new CalendarContext(),
            units,
            periods,
            Substitute.For<ICalculationBindingStore>());

        var descriptor = ScriptStand.Descriptor(mode) with { Engine = MethodologyEngine.Formulas };
        var formulaOutput = await generic.ExecuteAsync(
            new CalculationInput(
                descriptor, ScriptStand.DocumentId, 500, new PeriodKey(ScriptStand.PeriodValue), "R1", row),
            CancellationToken.None);

        var stand = new ScriptStand(pair.Script, mode);
        stand.AddTable("T1");
        var scriptOutput = await stand.RunArgumentsAsync(row);

        return (formulaOutput.Values, scriptOutput.Values);
    }

    private static void AssertSame(
        IReadOnlyList<CalculationOutputValue> formula, IReadOnlyList<CalculationOutputValue> script, string because)
    {
        static string Show(IReadOnlyList<CalculationOutputValue> values)
            => string.Join(
                "; ",
                values.OrderBy(v => v.OutputCode, StringComparer.Ordinal)
                    .Select(v => $"{v.OutputCode}|{v.SubstanceEntryId}|{v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}|{v.UnitId}|{v.Kind}"));

        Assert.True(Show(formula) == Show(script), $"{because}: формули [{Show(formula)}] ≠ скрипт [{Show(script)}]");
    }

    private static async Task CheckAsync(Pair pair, NumericMode mode)
    {
        foreach (var row in pair.Rows)
        {
            var (formula, script) = await RunBothAsync(pair, mode, row);
            AssertSame(formula, script, $"{mode}, рядок [{string.Join(',', row.Select(a => a.ArgumentCode + "=" + (a.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? a.ValueString)))}]");
        }
    }

    private static readonly Pair Division = new(
        ["Q", "R"],
        ["@A / @B", "@A + @B * 2"],
        "rows from T1;\noutput Q: kg = row.A / row.B;\noutput R: kg = row.A + row.B * 2;",
        [[N("A", 10m), N("B", 4m)], [N("A", 5m), N("B", 0m)], [N("A", 0m), N("B", 0m)], [N("A", 1m), N("B", 3m)]]);

    private static readonly Pair Branch = new(
        ["Q"],
        ["if(@A > 5, @A * 2, @A / 2)"],
        "rows from T1;\noutput Q: kg = row.A > 5 ? row.A * 2 : row.A / 2;",
        [[N("A", 3m)], [N("A", 8m)], [N("A", 5m)]]);

    private static readonly Pair RoundPow = new(
        ["Q"],
        ["Round(Pow(@A, 2) / 3, 4)"],
        "rows from T1;\noutput Q: kg = Round(Pow(row.A, 2) / 3, 4);",
        [[N("A", 7m)], [N("A", 1.5m)], [N("A", 10m)]]);

    private static readonly Pair TextCompare = new(
        ["Q"],
        ["if(@Kind = 'gas', @A * 1.5, @A * 0.5)"],
        "rows from T1;\noutput Q: kg = row.Kind == \"gas\" ? row.A * 1.5 : row.A * 0.5;",
        [[S("Kind", "gas"), N("A", 4m)], [S("Kind", "oil"), N("A", 4m)]]);

    private static readonly Pair Steps = new(
        ["T", "Q"],
        ["@A * @B", "if(in(@Code, 1, 2, 3), !T / 2, 0)"],
        "rows from T1;\nvar t = row.A * row.B;\noutput Q: kg = row.Code in (1, 2, 3) ? t / 2 : 0;",
        [[N("Code", 2m), N("A", 4m), N("B", 5m)], [N("Code", 9m), N("A", 4m), N("B", 5m)]]);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public Task Ділення_маскований_нуль_у_Legacy() => CheckAsync(Division, NumericMode.Legacy);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public Task Ділення_у_Strict() => CheckAsync(Division, NumericMode.Strict);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public Task Гілка_if_у_Legacy() => CheckAsync(Branch, NumericMode.Legacy);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public Task Гілка_if_у_Strict() => CheckAsync(Branch, NumericMode.Strict);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public Task Round_і_Pow_у_Legacy() => CheckAsync(RoundPow, NumericMode.Legacy);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public Task Round_і_Pow_у_Strict() => CheckAsync(RoundPow, NumericMode.Strict);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public Task Порівняння_тексту_у_Legacy() => CheckAsync(TextCompare, NumericMode.Legacy);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public Task Порівняння_тексту_у_Strict() => CheckAsync(TextCompare, NumericMode.Strict);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public Task Проміжні_кроки_й_in_у_Legacy() => CheckAsync(Steps, NumericMode.Legacy);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public Task Проміжні_кроки_й_in_у_Strict() => CheckAsync(Steps, NumericMode.Strict);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Нуль_справді_маскується_у_Legacy_і_лишається_помилкою_у_Strict()
    {
        var (legacyFormula, legacyScript) = await RunBothAsync(Division, NumericMode.Legacy, [N("A", 5m), N("B", 0m)]);
        var (strictFormula, strictScript) = await RunBothAsync(Division, NumericMode.Strict, [N("A", 5m), N("B", 0m)]);

        // Legacy: 5/0 → ∞ → 0 (маска); Strict: #DIV/0, виходу Q немає.
        Assert.Equal(0m, legacyScript.Single(v => v.OutputCode == "Q").Value);
        Assert.DoesNotContain(strictScript, v => v.OutputCode == "Q");
        Assert.Equal(legacyFormula.Count, legacyScript.Count);
        Assert.Equal(strictFormula.Count, strictScript.Count);
    }
}
