using Ecr.Expressions.Evaluation;

namespace Ecr.Expressions.Functions;

/// <summary>
/// <c>CONVERT(value, fromUnit, toUnit)</c> — **єдиний** спосіб змінити одиницю
/// у виразі. Рушій ніколи не конвертує неявно (D-74).
/// </summary>
public static class ConvertFunction
{
    /// <summary>Виконує конверсію через контекст.</summary>
    /// <param name="args">Значення, код вихідної одиниці, код цільової.</param>
    /// <param name="context">Джерело коефіцієнтів — <c>uom.*</c>.</param>
    /// <remarks>
    /// ⛔ Контекстні коефіцієнти сюди НЕ передаються. Перехід «м³ → кг»
    /// пишеться як <c>CONVERT(@Volume * CST.Density, 'kg', 't')</c>: щільність
    /// множиться явно і живе в константах методології зі своєю темпоральністю
    /// й версійністю (ФВ-16.5). Дозволити її тут означало б, що те саме число
    /// перетворюється по-різному залежно від того, хто заповнив довідник.
    /// </remarks>
    public static ExpressionValue Invoke(IReadOnlyList<ExpressionValue> args, IEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(context);

        if (args.Count != 3)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        // Помилка поширюється: CONVERT(1/0, 'kg', 't') має лишитися #DIV/0,
        // інакше причина зникне за кодом одиниць.
        if (args[0].IsError)
        {
            return args[0];
        }

        if (args[1].Value is not string from || args[2].Value is not string to)
        {
            // Одиниця, обчислена виразом, робить перевірку при публікації
            // неможливою: до запуску невідомо, у що саме конвертують.
            return ExpressionValue.Error(ExpressionErrors.BadUnit);
        }

        // null лишається null: «не заповнено» в інших одиницях — так само
        // «не заповнено», а не нуль.
        if (args[0].IsNull)
        {
            return ExpressionValue.Null;
        }

        if (args[0].AsNumber() is null)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return context.Convert(args[0], from, to);
    }
}
