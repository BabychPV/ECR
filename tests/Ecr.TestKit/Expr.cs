using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Expressions.Parsing;

namespace Ecr.TestKit;

/// <summary>
/// Розбір і обчислення виразу однією дією — щоб тест читався як твердження про
/// МОВУ, а не як складання рушія.
/// </summary>
public static class Expr
{
    /// <summary>Розбирає вираз.</summary>
    public static ParseResult Parse(string expression, ExpressionDialect dialect = ExpressionDialect.Template)
        => new Parser().Parse(expression, dialect);

    /// <summary>Обчислює вираз; кидає, якщо він не розібрався.</summary>
    /// <remarks>
    /// ⚠ Нерозібраний вираз — це помилка ТЕСТУ, а не результат: якщо вираз не
    /// компілюється, перевіряти його значення нема сенсу, і мовчазний
    /// <c>null</c> сховав би причину.
    /// </remarks>
    public static ExpressionValue Eval(
        string expression,
        IEvaluationContext? context = null,
        ExpressionDialect dialect = ExpressionDialect.Template)
    {
        var parsed = Parse(expression, dialect);
        if (!parsed.IsSuccess || parsed.Expression is null)
        {
            var reasons = string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Code} @{d.Position}: {d.Message}"));
            throw new InvalidOperationException($"Вираз не розібрався: {expression} — {reasons}");
        }

        return new Evaluator(new FunctionRegistry())
            .Evaluate(parsed.Expression.Root, context ?? new TestEvaluationContext());
    }

    /// <summary>Обчислює вираз і повертає число; зручно для арифметичних тестів.</summary>
    public static decimal Number(string expression, IEvaluationContext? context = null)
    {
        var value = Eval(expression, context);
        return value.AsNumber()
               ?? throw new InvalidOperationException(
                   $"Вираз '{expression}' дав не число, а {value.Type} ({value.ErrorCode}).");
    }
}
