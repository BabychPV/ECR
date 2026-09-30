// tests/Ecr.Calculations.Tests/Library/LibraryContextTests.cs
using Ecr.Domain.Enums;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Calculations.Tests.Library;

/// <summary>
/// HSE301 L1: контекст обчислення методології бере <c>!Code</c> бібліотеки через
/// резолвер і не зациклюється.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати захист <c>_resolving</c> у
/// <c>MethodologyEvaluationContext.GetFormulaResult</c> — обидва тести циклу червоні
/// (резолвер тесту кидає після 50 викликів замість <c>StackOverflow</c>, який убив би
/// процес тестів).
/// </remarks>
public sealed class LibraryContextTests
{
    private const int RecursionLimit = 50;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Без_резолвера_промах_це_REF_як_до_кроку()
    {
        var context = Context(resolve: null);

        Assert.Equal(ExpressionErrors.BadReference, context.GetFormulaResult("Common_X").ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Імя_що_нікуди_не_веде_це_REF()
    {
        var context = Context(_ => null);

        Assert.Equal(ExpressionErrors.BadReference, context.GetFormulaResult("Missing").ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Бібліотечне_значення_резолвиться_один_раз_на_контекст()
    {
        var calls = 0;
        var context = Context(name =>
        {
            calls++;
            return name == "Common_X" ? ExpressionValue.Number(42m) : null;
        });

        Assert.Equal(42m, context.GetFormulaResult("Common_X").AsNumber());
        Assert.Equal(42m, context.GetFormulaResult("common_x").AsNumber());

        // Друге посилання того самого рядка не рахує бібліотечну формулу вдруге.
        Assert.Equal(1, calls);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Своя_формула_перекриває_резолвер()
    {
        var context = Context(_ => throw new InvalidOperationException("Резолвер не мав питатися."));
        context.SetFormulaResult("Common_X", ExpressionValue.Number(7m));

        Assert.Equal(7m, context.GetFormulaResult("Common_X").AsNumber());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_в_одному_контексті_дає_CYCLE_а_не_рекурсію()
    {
        var calls = 0;
        MethodologyEvaluationContext context = null!;
        context = Context(name =>
        {
            Guard(ref calls);
            return name switch
            {
                "A" => context.GetFormulaResult("B"),
                "B" => context.GetFormulaResult("A"),
                _ => null,
            };
        });

        var value = context.GetFormulaResult("A");

        Assert.Equal(ExpressionErrors.RuntimeCycle, value.ErrorCode);
        Assert.Equal(ExpressionErrors.RuntimeCycle, context.GetFormulaResult("B").ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public void Цикл_через_два_контексти_дає_CYCLE()
    {
        // Викликач → бібліотека → викликач: так виглядає бібліотека, перевидана з
        // посиланням назад уже ПІСЛЯ публікації викликача.
        var calls = 0;
        MethodologyEvaluationContext caller = null!;
        MethodologyEvaluationContext library = null!;

        caller = Context(name =>
        {
            Guard(ref calls);
            return name == "Common_X" ? library.GetFormulaResult("Common_X") : null;
        });
        library = Context(name =>
        {
            Guard(ref calls);
            return name == "Common_X" ? caller.GetFormulaResult("Common_X") : null;
        });

        Assert.Equal(ExpressionErrors.RuntimeCycle, caller.GetFormulaResult("Common_X").ErrorCode);
    }

    private static void Guard(ref int calls)
    {
        if (++calls > RecursionLimit)
        {
            throw new InvalidOperationException("Рекурсія без захисту від циклу.");
        }
    }

    private static MethodologyEvaluationContext Context(Func<string, ExpressionValue?>? resolve)
        => new(
            new PeriodContext(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), CalendarMode.Actual, 2026, 1),
            new Dictionary<string, ExpressionValue>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ExpressionValue>(StringComparer.OrdinalIgnoreCase),
            UnitTable.Seed(),
            registries: null,
            resolve);
}
