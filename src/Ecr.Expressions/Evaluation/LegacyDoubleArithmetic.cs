using Ecr.Expressions.Ast;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Арифметика чинної системи: подвійна точність, семантика NCalc 1.3.8.
/// </summary>
/// <remarks>
/// ⛔ **Жодна константа поведінки тут не взята з припущення.** Кожна має
/// відповідник у <c>docs/legacy-ncalc-1.3.8.md</c>, а той — тест у
/// <c>tests/Ecr.Legacy.Probe</c>, який проганяє САМЕ ТОЙ пакет
/// (<c>NCalc 1.3.8</c>), що розгорнутий у чинній базі.
///
/// ⚠ Три поведінки виглядають як дефекти і відтворюються навмисно:
///
/// <list type="number">
/// <item><description><c>NaN</c> і <c>±∞</c> — **значення**, а не помилки.
/// Чинна система перетворює їх на нуль мовчки (<c>Utilities.cs:56-71</c>);
/// ми віддаємо їх далі, щоб той, хто зберігає, замаскував їх у нуль
/// **із записом причини** в трейс (<c>MaskedZero</c>). Число те саме —
/// тиша інша.</description></item>
/// <item><description>ділення на нуль дає <c>+∞</c>, а **не** виняток:
/// виміряно, <c>1/0 → ∞</c>. Той <c>catch (DivideByZeroException)</c> у
/// чинному коді для ділення на нуль не спрацьовує ніколи.</description></item>
/// <item><description>округлення — **банківське**.</description></item>
/// </list>
///
/// ⛔ Цілочисельного ділення тут НЕМАЄ, і це теж вимір, а не спрощення:
/// <c>365/31 = 11.774193548387096</c>, <c>12/163 = 0.0736196319018405</c>.
/// Директива №05 §7 вимагала відтворити <c>365/31 = 11</c> — такої поведінки
/// в NCalc 1.3.8 не існує ні з констант, ні з літералів.
/// </remarks>
public sealed class LegacyDoubleArithmetic : IEvaluationArithmetic
{
    /// <inheritdoc />
    /// <remarks>
    /// ⛔ <c>ToEven</c> як **факт**, а не як «поки не виміряно»: чинна збірка
    /// створює <c>new Expression(key)</c> без <c>EvaluateOptions</c>
    /// (<c>Utilities.cs:42</c>), отже <c>RoundAwayFromZero</c> не виставлений.
    /// Одна точка задання, один тест, який упаде, якщо це змінять непомітно.
    /// </remarks>
    public RoundingMode Rounding => RoundingMode.ToEven;

    /// <inheritdoc />
    public ExpressionValue Binary(ExpressionValue left, ExpressionValue right, BinaryOperator op)
    {
        if (left.AsDouble() is not { } a || right.AsDouble() is not { } b)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return op switch
        {
            BinaryOperator.Add => ExpressionValue.LegacyNumber(a + b),
            BinaryOperator.Subtract => ExpressionValue.LegacyNumber(a - b),
            BinaryOperator.Multiply => ExpressionValue.LegacyNumber(a * b),

            // ⛔ Без перевірки на нуль: `a / 0` у подвійній точності дає `±∞`,
            // і саме це число чинна система маскує в нуль. Повернути тут
            // `#DIV/0` означало б дати ІНШИЙ результат, ніж еталон.
            BinaryOperator.Divide => ExpressionValue.LegacyNumber(a / b),
            BinaryOperator.Modulo => ExpressionValue.LegacyNumber(a % b),

            // ⛔ У діалекті методологій `^` — побітове XOR, а не степінь, і
            // парсер відхиляє його ще при публікації (`ECR-CALC-0431`).
            // Сюди дійти неможливо; лишається як явна межа.
            BinaryOperator.Power => ExpressionValue.Error(ExpressionErrors.BadValue),

            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };
    }

    /// <inheritdoc />
    public ExpressionValue Round(ExpressionValue value, int digits)
        => value.AsDouble() is { } d
            ? ExpressionValue.LegacyNumber(Math.Round(d, digits, MidpointRounding.ToEven))
            : ExpressionValue.Error(ExpressionErrors.BadValue);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ <c>Ceiling</c>, <c>Floor</c> і <c>Truncate</c> — строго одномісні:
    /// другого аргументу <c>significance</c> в NCalc 1.3.8 немає (виміряно).
    /// </remarks>
    public ExpressionValue Unary(ExpressionValue value, string function)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (value.AsDouble() is not { } a)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        // ⚠ Порівняння з урахуванням регістру: у чинній збірці
        // `EvaluateOptions.IgnoreCase` не виставлений, тож `abs` і `Abs` —
        // різні імена, і перше є помилкою.
        return function switch
        {
            "Abs" => ExpressionValue.LegacyNumber(Math.Abs(a)),
            "Acos" => ExpressionValue.LegacyNumber(Math.Acos(a)),
            "Asin" => ExpressionValue.LegacyNumber(Math.Asin(a)),
            "Atan" => ExpressionValue.LegacyNumber(Math.Atan(a)),
            "Ceiling" => ExpressionValue.LegacyNumber(Math.Ceiling(a)),
            "Cos" => ExpressionValue.LegacyNumber(Math.Cos(a)),
            "Exp" => ExpressionValue.LegacyNumber(Math.Exp(a)),
            "Floor" => ExpressionValue.LegacyNumber(Math.Floor(a)),
            "Log10" => ExpressionValue.LegacyNumber(Math.Log10(a)),
            "Sign" => ExpressionValue.LegacyNumber(Math.Sign(a)),
            "Sin" => ExpressionValue.LegacyNumber(Math.Sin(a)),

            // ⚠ `Sqrt(-1)` дає `NaN`, а не помилку — виміряно. Маскування в
            // нуль робить той, хто зберігає, і робить із записом причини.
            "Sqrt" => ExpressionValue.LegacyNumber(Math.Sqrt(a)),
            "Tan" => ExpressionValue.LegacyNumber(Math.Tan(a)),
            "Truncate" => ExpressionValue.LegacyNumber(Math.Truncate(a)),

            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };
    }

    /// <inheritdoc />
    public ExpressionValue Binary(ExpressionValue left, ExpressionValue right, string function)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (left.AsDouble() is not { } a || right.AsDouble() is not { } b)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return function switch
        {
            // ⚠ `IEEERemainder` — НЕ оператор `%`: знак і правило інші.
            // `IEEERemainder(5,3) = -1`, тоді як `5 % 3 = 2`.
            "IEEERemainder" => ExpressionValue.LegacyNumber(Math.IEEERemainder(a, b)),

            // ⚠ Логарифм `a` за основою `b`; одномісного виклику в 1.3.8
            // немає, тому натуральний логарифм у чинній системі недосяжний.
            "Log" => ExpressionValue.LegacyNumber(Math.Log(a, b)),
            "Max" => ExpressionValue.LegacyNumber(Math.Max(a, b)),
            "Min" => ExpressionValue.LegacyNumber(Math.Min(a, b)),
            "Pow" => ExpressionValue.LegacyNumber(Math.Pow(a, b)),

            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };
    }
}
