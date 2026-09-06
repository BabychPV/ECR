using Ecr.Expressions.Ast;
using Ecr.Expressions.Functions;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Наскрізний <see cref="decimal"/> — режим <c>Strict</c> і діалект шаблонів.
/// </summary>
/// <remarks>
/// ⛔ Тут доречний <see cref="DecimalMath"/> (<c>D4-15</c>) — і **лише** тут.
/// Раніше він обслуговував обидва режими, і це була коректна реалізація
/// неправильної вимоги: `Legacy` мусить відтворювати чинні числа, а не
/// рахувати краще за них.
///
/// ⚠ Вмикається **явним рішенням і лише з нової <c>ValidFrom</c>**
/// (`B13` §8 п.2). Причина: у нової методології немає минулих форм, які треба
/// відтворити, — а стартувати її в <c>Legacy</c> означало б свідомо
/// успадкувати пастки NCalc без жодної вигоди.
///
/// ⛔ Помилки тут — **значення**, а не винятки і не <c>NaN</c>: одна зіпсована
/// комірка не має валити перерахунок усієї таблиці (`02b` §6.4). Це і є
/// друга відмінність від <see cref="LegacyDoubleArithmetic"/>, крім типу:
/// там ділення на нуль дає нескінченність, тут — <c>#DIV/0</c>.
/// </remarks>
public sealed class StrictDecimalArithmetic : IEvaluationArithmetic
{
    /// <inheritdoc />
    /// <remarks>
    /// ⚠ <c>AwayFromZero</c>, бо так рахує чинна система на аркуші Excel
    /// (`ФВ-9.16a`), і банківське дало б інші числа на кожному «.5».
    /// </remarks>
    public RoundingMode Rounding => RoundingMode.AwayFromZero;

    /// <inheritdoc />
    public ExpressionValue Binary(ExpressionValue left, ExpressionValue right, BinaryOperator op)
    {
        if (left.AsNumber() is not { } a || right.AsNumber() is not { } b)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return op switch
        {
            BinaryOperator.Add => ExpressionValue.Number(a + b),
            BinaryOperator.Subtract => ExpressionValue.Number(a - b),
            BinaryOperator.Multiply => ExpressionValue.Number(a * b),

            // ⛔ Ділення на нуль — ЗНАЧЕННЯ-помилка, а не нескінченність:
            // у `decimal` нескінченності не існує, і мовчазний нуль тут був би
            // невідрізненний від справжнього.
            BinaryOperator.Divide => b == 0m
                ? ExpressionValue.Error(ExpressionErrors.DivideByZero)
                : ExpressionValue.Number(a / b),
            BinaryOperator.Modulo => b == 0m
                ? ExpressionValue.Error(ExpressionErrors.DivideByZero)
                : ExpressionValue.Number(a % b),

            BinaryOperator.Power => ExpressionValue.Number(DecimalMath.Pow(a, b)),

            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };
    }

    /// <inheritdoc />
    public ExpressionValue Round(ExpressionValue value, int digits)
        => value.AsNumber() is { } d
            ? ExpressionValue.Number(decimal.Round(d, digits, MidpointRounding.AwayFromZero))
            : ExpressionValue.Error(ExpressionErrors.BadValue);

    /// <inheritdoc />
    public ExpressionValue Unary(ExpressionValue value, string function)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (value.AsNumber() is not { } a)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return function switch
        {
            "Abs" => ExpressionValue.Number(Math.Abs(a)),
            "Ceiling" => ExpressionValue.Number(decimal.Ceiling(a)),
            "Floor" => ExpressionValue.Number(decimal.Floor(a)),
            "Sign" => ExpressionValue.Number(Math.Sign(a)),
            "Truncate" => ExpressionValue.Number(decimal.Truncate(a)),

            "Exp" => ExpressionValue.Number(DecimalMath.Exp(a)),
            "Ln" => a > 0m
                ? ExpressionValue.Number(DecimalMath.Ln(a))
                : ExpressionValue.Error(ExpressionErrors.BadValue),
            "Log10" => a > 0m
                ? ExpressionValue.Number(DecimalMath.Log10(a))
                : ExpressionValue.Error(ExpressionErrors.BadValue),
            "Sqrt" => a >= 0m
                ? ExpressionValue.Number(DecimalMath.Sqrt(a))
                : ExpressionValue.Error(ExpressionErrors.BadValue),

            // ⚠ Тригонометрії в `decimal` немає і не буде: у корпусі чинної
            // системи жодного її входження, а власна реалізація рядів заради
            // нуля викликів — це код, який ніхто не перевірить.
            //
            // ⛔ Наслідок названий прямо: `Sin`, `Cos`, `Tan`, `Asin`, `Acos`,
            // `Atan` є в каталозі (їх знає NCalc 1.3.8), розбираються і в
            // `Legacy` рахуються — а в `Strict` дають #VALUE. Це помилка
            // РЕЖИМУ, а не мови: перевести версію в `Strict` означає відмовитися
            // від тригонометрії, і побачити це треба на першому ж прогоні
            // золотого набору, а не в шостому знаку звірки.
            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };
    }

    /// <inheritdoc />
    public ExpressionValue Binary(ExpressionValue left, ExpressionValue right, string function)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (left.AsNumber() is not { } a || right.AsNumber() is not { } b)
        {
            return ExpressionValue.Error(ExpressionErrors.BadValue);
        }

        return function switch
        {
            "Max" => ExpressionValue.Number(Math.Max(a, b)),
            "Min" => ExpressionValue.Number(Math.Min(a, b)),
            "Pow" => ExpressionValue.Number(DecimalMath.Pow(a, b)),

            // ⚠ Логарифм `a` за основою `b` — саме двомісний, як у чинному
            // рушії: одномісного виклику в NCalc 1.3.8 немає, тому й тут його
            // немає. Основа 1 дала б ділення на нуль, від'ємна або нульова —
            // невизначений результат; обидва випадки — #VALUE, а не виняток.
            "Log" => a > 0m && b > 0m && b != 1m
                ? ExpressionValue.Number(DecimalMath.Ln(a) / DecimalMath.Ln(b))
                : ExpressionValue.Error(ExpressionErrors.BadValue),

            // ⚠ `IEEERemainder` — НЕ оператор `%`: остача береться від
            // НАЙБЛИЖЧОГО частого, тому знак інший. `IEEERemainder(5,3) = -1`,
            // тоді як `5 % 3 = 2`. Округлення частки — банківське, як вимагає
            // сам стандарт IEEE 754, і воно не залежить від `Rounding` цього
            // режиму: правило належить функції, а не режиму.
            "IEEERemainder" => b == 0m
                ? ExpressionValue.Error(ExpressionErrors.DivideByZero)
                : ExpressionValue.Number(a - (b * decimal.Round(a / b, 0, MidpointRounding.ToEven))),

            _ => ExpressionValue.Error(ExpressionErrors.BadValue),
        };
    }
}
