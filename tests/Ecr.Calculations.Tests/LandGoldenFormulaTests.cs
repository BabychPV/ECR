using System.Globalization;
using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Graph;
using Ecr.Expressions.Parsing;
using Ecr.Infrastructure.Expressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests;

/// <summary>
/// Золоті числа методологій Land (<c>ECW_C05_01…05</c>, <c>ECW_C06_01</c>):
/// формула методології в діалекті <see cref="ExpressionDialect.Methodology"/>
/// з підставленими вхідними й коефіцієнтами, очікуване число — незалежний ручний
/// розрахунок зі специфікації Land, а не вихід самого коду.
/// </summary>
/// <remarks>
/// ⚠ Вхідні дані ВИГАДАНІ; коефіцієнти — публічні значення методики з довідки
/// специфікації. Прод-даних тут немає. Тести перевіряють запис формули й
/// арифметику рушія, а не вибір категорії константи (це окрема тема, D-2).
/// ⚠ Кейси 5.4 і 5.5 мають ПРИПУЩЕНІ вхідні (KNO2, Dens, LHV, Merc, H2S) — у
/// специфікації їх немає; число доводить лише збіг формули з ручним розрахунком.
/// </remarks>
public sealed class LandGoldenFormulaTests
{
    private static readonly FormulaEngine Engine =
        new(new Parser(), new Evaluator(new FunctionRegistry()), new TopologicalSorter());

    // Очікувані числа округлені до ~6 значущих цифр, тож допуск відносний.
    private const decimal RelativeTolerance = 0.00001m;

    private static string N(decimal v) => v.ToString(CultureInfo.InvariantCulture);

    private static decimal Calc(string expression)
    {
        var parsed = Engine.Parse(expression, ExpressionDialect.Methodology);
        Assert.True(parsed.IsSuccess, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        var value = Engine.Evaluate(parsed.Expression!, new TestEvaluationContext()).Value;
        return value.AsNumber()
               ?? throw new InvalidOperationException($"'{expression}' → {value.Type} ({value.ErrorCode})");
    }

    private static void Close(decimal expected, decimal actual) =>
        Assert.True(Math.Abs(expected - actual) <= Math.Abs(expected) * RelativeTolerance,
            $"очікувано {expected}, отримано {actual}");

    // ---- 5.1 (P1): Generator + Diesel, Not passed overhaul, Power=500 → Loc_BeforeMR_B ----
    // EF Loc_BeforeMR_B: gsec NOx 9.6, 328 0.5, 330 1.2, 337 6.2; tons NOx 40, 328 2, 330 5, 337 26.

    [Theory] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [InlineData(20, 0.8, 0.64, 0.104, 0.04, 0.1, 0.52)]
    [InlineData(30, 1.2, 0.96, 0.156, 0.06, 0.15, 0.78)]
    public void P1_5_1_тонни_залежать_від_пального_за_тією_самою_формулою(
        decimal fuel, decimal nox, decimal t301, decimal t304, decimal t328, decimal t330, decimal t337)
    {
        var f = N(fuel);
        var tNox = Calc($"40 * {f} / (1000 * 1)");

        Close(nox, tNox);
        Close(t301, Calc($"{N(tNox)} / 100 * 80"));
        Close(t304, Calc($"{N(tNox)} / 100 * 13"));
        Close(t328, Calc($"2 * {f} / 1000"));
        Close(t330, Calc($"5 * {f} / 1000"));
        Close(t337, Calc($"26 * {f} / 1000"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void P1_5_1_грами_за_секунду_від_потужності()
    {
        const string power = "500";

        var gNox = Calc($"9.6 * {power} / 3600");
        Close(1.333333m, gNox);
        Close(1.066667m, Calc($"{N(gNox)} / 100 * 80"));
        Close(0.173333m, Calc($"{N(gNox)} / 100 * 13"));
        Close(0.069444m, Calc($"0.5 * {power} / 3600"));
        Close(0.166667m, Calc($"1.2 * {power} / 3600"));
        Close(0.861111m, Calc($"6.2 * {power} / 3600"));
    }

    // ---- 5.2 (P2): бензин, червень (Summer), Hours=50 ----

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void P2_5_2_бензин_літо()
    {
        var tNox = Calc("50 * 5 * 0.25 * 0.14 / 1000000");
        Close(0.00000875m, tNox);
        Close(0.000007m, Calc($"{N(tNox)} * 0.8"));
        Close(0.0000011375m, Calc($"{N(tNox)} * 0.13"));
        Close(0.000048611m, Calc("5 * 0.25 * 0.14 / 3600"));
        Close(0.00046875m, Calc("50 * 5 * 0.25 * 7.5 / 1000000"));
    }

    // ---- 5.3 (P3): бензорез, Hours=10 ----

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void P3_5_3_бензорез()
    {
        var tNox = Calc("10 * 0.01 * 60 / 1000000");
        Close(0.000006m, tNox);
        Close(0.0000048m, Calc($"{N(tNox)} * 0.8"));
        Close(0.00016667m, Calc("0.01 / 60"));
    }

    // ---- 5.4: дизельний котел. ⚠ KNO2=0.08 ПРИПУЩЕНО (у специфікації немає) ----

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void K5_4_дизельний_котел_припущені_вхідні()
    {
        // B=2 т, Qir=42.75 (AF), ⚠KNO2=0.08
        Close(0.005472m, Calc("0.8 * 0.001 * 2 * 42.75 * 0.08"));
        Close(0.0005m, Calc("2 * 0.025 * 0.01"));
    }

    // ---- 5.5: газовий котел. ⚠ Dens=0.75, LHV=36, KNO2=0.1, Merc=0.5, H2S=2, Gas=100000 ПРИПУЩЕНО ----

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void K5_5_газовий_котел_припущені_вхідні()
    {
        const string mass = "(100000 * 0.75 / 1000)";   // 75
        const string heat = "(36 / 0.75)";               // 48

        Close(0.288m, Calc($"0.8 * 0.001 * {mass} * {heat} * 0.1 * (1 - 0)"));
        Close(0.9m, Calc($"0.001 * {mass} * {heat} * 0.25 * (1 - 0 / 100)"));
        Close(3.57m, Calc($"{mass} * (0.02 * 0.5 * 1 * 1 + 1.88 * 0.01 * 2)"));
    }

    // ---- 6.1: мобільна техніка, Diesel ----

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void K6_1_мобільна_дизель_B5()
    {
        // EF Diesel: 301 0.01; V1 множить ще на кількість техніки N=1.
        Close(0.05m, Calc("0.01 * 5 * 1"));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void P5_6_1_мобільна_дизель_2_5_тонни_40_кг_за_годину()
    {
        Close(0.025m, Calc("0.01 * 2.5"));
        Close(0.05m, Calc("0.02 * 2.5"));
        Close(0.25m, Calc("0.1 * 2.5"));
        Close(0.03875m, Calc("0.0155 * 2.5"));
        Close(0.075m, Calc("0.03 * 2.5"));
        Close(0.111111m, Calc("0.01 * 40 / 3.6"));
        Close(1.111111m, Calc("0.1 * 40 / 3.6"));
    }
}
