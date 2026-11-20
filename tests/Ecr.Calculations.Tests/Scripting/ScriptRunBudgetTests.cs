using System.Globalization;
using System.Text;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// PSS-21: межі прогону скрипта понад стелю одного оператора — бюджет РЯДКА
/// (<see cref="ScriptCalculationModule.MaxRowEvaluationSteps"/>), спільний бюджет запиту, стеля величин перегляду й
/// скасування між операторами.
/// </summary>
/// <remarks>
/// Важкий оператор — вкладений агрегат над двома псевдонімами однієї таблиці на 150 рядків: ~150×150 звернень, тобто
/// кожен впирається у стелю оператора (<see cref="Evaluator.MaxEvaluationSteps"/>) і витрачає рівно її. Сто таких
/// операторів вичерпують бюджет рядка; дешевий вихід після них показує, чи рядок ще рахує.
/// </remarks>
public sealed class ScriptRunBudgetTests
{
    private const int HeavyRows = 150;

    private static string Script(int heavyStatements)
    {
        var text = new StringBuilder("rows from T1;\ntable G = T2;\ntable H = T2;\n");
        for (var i = 0; i < heavyStatements; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"var v{i} = Count(G where Count(H where H.K == G.K) > 0);\n");
        }

        return text.Append("output O: kg = 1;\n").ToString();
    }

    private static ScriptStand Stand(int heavyStatements)
    {
        var stand = new ScriptStand(Script(heavyStatements), NumericMode.Strict);
        stand.AddTable("T1").Column("K", CellDataType.String).Row("R1", ("K", "a")).Row("R2", ("K", "b"));
        var alias = stand.AddTable("T2").Column("K", CellDataType.String);
        for (var i = 0; i < HeavyRows; i++)
        {
            alias.Row("G" + i.ToString(CultureInfo.InvariantCulture), ("K", "k" + (i % 7).ToString(CultureInfo.InvariantCulture)));
        }

        return stand;
    }

    private static ScriptValue Output(IReadOnlyList<ScriptValue> values) => values.Single(v => v.Name == "O");

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-21")]
    public async Task Важкий_оператор_витрачає_рівно_стелю_оператора_а_рядок_до_межі_рахує_вихід()
    {
        // Контроль стенда: оператор справді впирається у стелю (інакше тест нижче нічого б не доводив).
        var (_, single) = await Stand(1).RunAsync();
        Assert.Equal(ExpressionErrors.BudgetExceeded, single.Single(v => v.Name == "v0").ErrorCode);

        // 50 важких операторів = 1 000 000 кроків < бюджету рядка: вихід пораховано.
        var (output, values) = await Stand(50).RunAsync();
        Assert.Null(Output(values).ErrorCode);
        Assert.Equal(1m, ScriptStand.Value(output, "O"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-21")]
    public async Task Рядок_що_вичерпав_бюджет_дає_BUDGET_на_кожен_наступний_оператор_і_stepError()
    {
        // 120 × 20 000 = 2 400 000 > 2 000 000: після сотого оператора рядок більше не рахує нічого.
        // Мутація: бюджет лише на оператор (без `_row`) — вихід O = 1, StepError лишається тільки від важких операторів.
        var (output, values) = await Stand(120).RunAsync();

        Assert.Equal(ExpressionErrors.BudgetExceeded, Output(values).ErrorCode);
        Assert.Null(ScriptStand.Value(output, "O"));
        Assert.NotNull(output.StepError);
        Assert.Equal(2_000_000, ScriptCalculationModule.MaxRowEvaluationSteps);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-21")]
    public async Task Спільний_бюджет_запиту_ділиться_між_рядками_а_стеля_величин_обрізає_перелік()
    {
        var stand = Stand(60);
        var context = await stand.Module.PrepareAsync(
            stand.Methodology, ScriptStand.DocumentId, new Ecr.Domain.ValueObjects.PeriodKey(ScriptStand.PeriodValue), CancellationToken.None);
        var pool = new EvaluationBudget(1_500_000);
        var limits = new ScriptRunLimits(pool, MaxValues: int.MaxValue);

        // Рядок 1: 60 × 20 000 = 1 200 000 — вкладається, вихід пораховано; зі спільного списано витрачене.
        var (_, first) = await stand.Module.ExecuteDetailedAsync(context, await stand.InputAsync("R1"), limits, CancellationToken.None);
        Assert.Null(Output(first).ErrorCode);
        Assert.InRange(pool.Spent, 1_200_000, 1_200_100);

        // Рядок 2: лишилося ~300 000 — вичерпано на 15-му операторі, вихід #BUDGET.
        // Мутація: не списувати витрачене рядком зі спільного (`limits.Pool?.TryConsume`) — вихід рядка 2 = 1.
        var (_, second) = await stand.Module.ExecuteDetailedAsync(context, await stand.InputAsync("R2"), limits, CancellationToken.None);
        Assert.Equal(ExpressionErrors.BudgetExceeded, Output(second).ErrorCode);
        Assert.True(pool.IsExhausted);

        // Стеля величин: зібрано рівно стільки, скільки дозволено.
        // Мутація: прибрати перевірку `detail.Count >= MaxValues` — 61 величина.
        var (_, capped) = await stand.Module.ExecuteDetailedAsync(
            context, await stand.InputAsync("R1"), new ScriptRunLimits(MaxValues: 3), CancellationToken.None);
        Assert.Equal(3, capped.Count);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-21")]
    public async Task Скасований_токен_зупиняє_прогін_рядка_між_операторами()
    {
        var stand = Stand(3);
        var context = await stand.Module.PrepareAsync(
            stand.Methodology, ScriptStand.DocumentId, new Ecr.Domain.ValueObjects.PeriodKey(ScriptStand.PeriodValue), CancellationToken.None);
        var input = await stand.InputAsync("R1");
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        // Мутація: прибрати `ct.ThrowIfCancellationRequested()` з `ScriptRowRun.Execute` — рядок рахується до кінця.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stand.Module.ExecuteDetailedAsync(context, input, aborted.Token));
    }
}
