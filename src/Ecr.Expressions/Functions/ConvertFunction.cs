using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Functions;

/// <summary>
/// <c>CONVERT(value, fromUnit, toUnit)</c> — **єдиний** спосіб змінити одиницю
/// у виразі. Рушій ніколи не конвертує неявно (D-74).
/// </summary>
public static class ConvertFunction
{
    /// <summary>Виконує конверсію через контекст.</summary>
    public static ExpressionValue Invoke(IReadOnlyList<ExpressionValue> args, IEvaluationContext context)
        => throw new NotImplementedException(
            "TODO: перевірити 3 аргументи; 2-й і 3-й — текстові коди одиниць; " +
            "делегувати context.Convert. Різні розмірності → ExpressionValue.Error(\"#UNIT\"). " +
            "Контекстні коефіцієнти (щільність) сюди НЕ передаються: перехід м³ → кг робиться " +
            "як CONVERT(@Volume * CST.Density, 'kg', 't') (ФВ-16.5).");
}
