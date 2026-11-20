using System.Globalization;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Scripting;

/// <summary>
/// PSS-21: межі прогону скрипта понад стелю одного оператора — бюджет РЯДКА
/// (<see cref="CalculationLimits.MaxRowEvaluationSteps"/>, з конфігурації), спільний бюджет запиту, стеля величин
/// перегляду, скасування між операторами, <c>reject when</c> із вичерпаним бюджетом і прапорець вичерпаного бюджету
/// перегляду.
/// </summary>
/// <remarks>
/// Важкий оператор — вкладений агрегат над двома псевдонімами однієї таблиці на 150 рядків: ~150×150 звернень, тобто
/// кожен впирається у стелю оператора (<see cref="Evaluator.MaxEvaluationSteps"/>) і витрачає рівно її. Стенд ставить
/// бюджет рядка 2 000 000 (<see cref="RowSteps"/>) — сто таких операторів його вичерпують; дешевий вихід після них
/// показує, чи рядок ще рахує.
/// </remarks>
public sealed class ScriptRunBudgetTests
{
    private const int HeavyRows = 150;

    /// <summary>Бюджет рядка стенда — сто стель оператора (типовий у продукті — тисяча).</summary>
    private const int RowSteps = 100 * Evaluator.MaxEvaluationSteps;

    private const string Tail = "output O: kg = 1;\n";

    private static string Script(int heavyStatements, string tail = Tail, string head = "")
    {
        var text = new StringBuilder("rows from T1;\ntable G = T2;\ntable H = T2;\n").Append(head);
        for (var i = 0; i < heavyStatements; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $"var v{i} = Count(G where Count(H where H.K == G.K) > 0);\n");
        }

        return text.Append(tail).ToString();
    }

    private static ScriptStand Stand(int heavyStatements, string tail = Tail, int? rowSteps = RowSteps, string head = "")
        => Arrange(new ScriptStand(Script(heavyStatements, tail, head), NumericMode.Strict)
        {
            Limits = rowSteps is { } steps ? new CalculationLimits { MaxRowEvaluationSteps = steps } : null,
        });

    /// <summary>Таблиця рядків T1 (R1: K = "a", R2: K = "b") і таблиця-псевдонім T2 на <see cref="HeavyRows"/> рядків.</summary>
    private static ScriptStand Arrange(ScriptStand stand)
    {
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
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-21")]
    public async Task Бюджет_рядка_береться_з_конфігурації_типово_20_мільйонів()
    {
        Assert.Equal(20_000_000, CalculationLimits.DefaultMaxRowEvaluationSteps);
        Assert.Equal(20_000_000, new CalculationLimits().MaxRowEvaluationSteps);

        // Той самий скрипт: з типовим бюджетом (20 млн) 120 важких операторів (2,4 млн) вкладаються й вихід пораховано…
        // Мутація: ScriptRowRun бере стару константу (2 млн) замість CalculationLimits — вихід #BUDGET.
        var (byDefault, defaultValues) = await Stand(120, rowSteps: null).RunAsync();
        Assert.Null(Output(defaultValues).ErrorCode);
        Assert.Equal(1m, ScriptStand.Value(byDefault, "O"));
        Assert.Null(byDefault.StepError);

        // …а з налаштованим (2 млн) — ні: детерміновано, за роботою, а не часом.
        var (configured, configuredValues) = await Stand(120, rowSteps: 2_000_000).RunAsync();
        Assert.Equal(ExpressionErrors.BudgetExceeded, Output(configuredValues).ErrorCode);
        Assert.NotNull(configured.StepError);

        // Менше за стелю одного оператора — недійсно (старт служби зупиняє EcrConfigurationValidation).
        Assert.Equal(Evaluator.MaxEvaluationSteps, CalculationLimits.MinRowEvaluationSteps);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CalculationLimits { MaxRowEvaluationSteps = Evaluator.MaxEvaluationSteps - 1 }.EnsureValid());
        var edge = new CalculationLimits { MaxRowEvaluationSteps = Evaluator.MaxEvaluationSteps };
        Assert.Same(edge, edge.EnsureValid());
    }

    /// <summary>
    /// Рецензія PSS, п. 4: умова <c>reject when</c> не порахувалася, бо бюджет рядка вичерпано, — рядок НЕ «відхилено
    /// правилом» (невідомо, чи скрипт його відхилив би), а помилка кроку <c>#BUDGET</c>: жодного числа, <c>StepError</c>.
    /// </summary>
    /// <remarks>
    /// Мутація: прибрати гілку <c>BudgetExceeded</c> у <c>ScriptRowRun.Reject</c> — виняток <c>categoryRuleFailed</c>
    /// (рядок «відхилено»).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-21")]
    public async Task Reject_після_вичерпаного_бюджету_рядка_дає_BUDGET_і_StepError_а_не_відхилення()
    {
        // Вихід ДО відмови вже пораховано — відмова з невідомим результатом скасовує й його; вихід після — не рахується.
        var stand = Stand(120, tail: "reject when row.K == \"zzz\";\n" + Tail, head: "output A: kg = 1;\n");

        var (output, values) = await stand.RunAsync();

        Assert.NotNull(output.StepError);
        Assert.Equal(ExpressionErrors.BudgetExceeded, output.StepError!.ErrorCode);
        Assert.Equal(2, output.StepError.FailedSteps); // A і O — обидва виходи без результату
        Assert.Empty(output.Values);
        Assert.DoesNotContain(values, v => v.Name == "O");
        Assert.Contains(output.Trace, s => s.StepCode == "reject");

        // Контроль: та сама відмова з бюджетом — справжнє відхилення правилом (рядок R1 має K = "a").
        var rejecting = Stand(0, tail: "reject when row.K == \"a\";\n" + Tail);
        var rejected = await Assert.ThrowsAsync<Ecr.Domain.Abstractions.DomainException>(() => rejecting.RunAsync());
        Assert.Equal("err.ECR-CALC-0422.categoryRuleFailed", rejected.Details!["messageKey"]);
    }

    /// <summary>
    /// Рецензія PSS, M2: вичерпаний СПІЛЬНИЙ бюджет перегляду позначено у відповіді (<c>budgetExhausted</c>) — інакше
    /// <c>#BUDGET</c> пізніх рядків не відрізнити від помилки скрипта; вичерпаний бюджет РЯДКА (є і в перерахунку) прапорця
    /// не ставить.
    /// </summary>
    /// <remarks>Мутація: <c>BudgetExhausted = false</c> у <c>ScriptPreviewRunner.RunAsync</c> — перше твердження червоне.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Finding", "PSS-21")]
    public async Task Перегляд_позначає_вичерпаний_спільний_бюджет_а_не_бюджет_рядка()
    {
        var stand = Stand(60);
        static ScriptPreviewRequest Request(ScriptStand s)
            => new(ScriptStand.VersionId, s.Text, ScriptStand.DocumentId, ScriptStand.PeriodValue, TableInstanceId: null, MaxRows: 50);

        // 1 500 000 на запит: R1 бере 1 200 000, R2 вичерпує решту.
        var exhausted = await stand.PreviewRunner(previewSteps: 1_500_000).RunAsync(Request(stand), CancellationToken.None);
        Assert.True(exhausted.BudgetExhausted);
        var rows = Assert.Single(exhausted.Tables).Rows;
        Assert.Null(rows.Single(r => r.RowKey == "R1").Issue);
        Assert.Equal("stepError", rows.Single(r => r.RowKey == "R2").Issue);

        // Типовий бюджет перегляду (20 млн): обидва рядки пораховано, прапорця немає.
        var whole = await stand.PreviewRunner().RunAsync(Request(stand), CancellationToken.None);
        Assert.False(whole.BudgetExhausted);
        Assert.All(Assert.Single(whole.Tables).Rows, r => Assert.Null(r.Issue));

        // Бюджет РЯДКА вичерпано (120 × 20 000 > 2 000 000) — stepError, але це помилка скрипта, не межа перегляду.
        var heavy = Stand(120);
        var rowLimited = await heavy.PreviewRunner().RunAsync(Request(heavy), CancellationToken.None);
        Assert.False(rowLimited.BudgetExhausted);
        Assert.All(Assert.Single(rowLimited.Tables).Rows, r => Assert.Equal("stepError", r.Issue));
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
