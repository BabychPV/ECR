using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;

namespace Ecr.Calculations;

/// <summary>Значення виходу разом із причиною, якщо воно замасковане.</summary>
/// <param name="Value">
/// Що піде в <c>calc.CalculationResult</c>; <c>null</c> — не піде нічого.
/// </param>
/// <param name="Reason">Причина маскування; <see cref="MaskedZeroReason.None"/> — його не було.</param>
public readonly record struct MaskedOutput(decimal? Value, MaskedZeroReason Reason);

/// <summary>
/// Маскування <c>NaN</c>/<c>±∞</c> на межі зберігання результату.
/// </summary>
/// <remarks>
/// ⛔ Маскування живе **тут**, а не в арифметиці. <c>LegacyDoubleArithmetic</c>
/// віддає <c>NaN</c> і <c>±∞</c> як **значення** — інакше проміжний крок уже
/// був би нулем, і формула, що ділить на цей крок, дала б <c>+∞</c> замість
/// нуля, тобто ІНШЕ число, ніж еталон.
///
/// ✎ D-427 (закриває Q-088): чинна система маскує КОЖНУ формулу окремо
/// (<c>Utilities.cs:58-71</c>), тому в <c>Legacy</c> маска ставиться на результаті
/// кожної формули (<c>GenericCalculationModule.Evaluate</c>), а <see cref="Prepare"/>
/// на виході методології лишається останньою межею (і єдиною для <c>Strict</c>,
/// де проміжний #DIV/0 — помилка-значення, що поширюється).
/// </remarks>
public static class MaskedZero
{
    /// <summary>
    /// Готує значення виходу до запису.
    /// </summary>
    /// <param name="value">Що дав рушій.</param>
    /// <param name="mode">Режим версії методології.</param>
    /// <returns>Значення й причина маскування.</returns>
    public static MaskedOutput Prepare(ExpressionValue value, NumericMode mode)
    {
        // Звичайний шлях: число є, маскувати нема чого.
        if (value.AsNumber() is { } number)
        {
            return new MaskedOutput(number, MaskedZeroReason.None);
        }

        var reason = ReasonFor(value);

        if (reason == MaskedZeroReason.None)
        {
            // Не число і не маска — текст, дата, помилка-значення. Причина
            // такого виходу вже в трейсі окремим кроком.
            return new MaskedOutput(null, MaskedZeroReason.None);
        }

        // ⛔ `Legacy` віддає НУЛЬ — те саме число, що й чинна система. Інакше
        // звірка розійшлася б на кожному такому рядку, і розбіжність довелося
        // б пояснювати як наш дефект, хоча ми лише перестали мовчати.
        //
        // ⛔ `Strict` віддає `null`, а НЕ нуль (`ФВ-9.14`). Нуль там був би
        // гірший за відсутність: він виглядає як виміряне значення, і побачити
        // різницю можна лише в трейсі, куди на цьому режимі ніхто не дивиться.
        return mode == NumericMode.Legacy
            ? new MaskedOutput(0m, reason)
            : new MaskedOutput(null, reason);
    }

    /// <summary>Чи є значення тим, що чинна система маскує в нуль.</summary>
    /// <param name="value">Значення рушія.</param>
    /// <returns>Причина або <see cref="MaskedZeroReason.None"/>.</returns>
    /// <remarks>
    /// ⚠ Питається <see cref="ExpressionValue.AsDouble"/>, а не тип: маска
    /// можлива лише в подвійній точності, бо в <c>decimal</c> ані <c>NaN</c>,
    /// ані нескінченності не існує. Саме тому маскування недосяжне, доки
    /// версія не дістала <see cref="NumericMode.Legacy"/> — і саме тому цей
    /// крок робиться разом із підстановкою арифметики, а не після неї.
    /// </remarks>
    public static MaskedZeroReason ReasonFor(ExpressionValue value)
    {
        if (value.AsDouble() is not { } d)
        {
            return MaskedZeroReason.None;
        }

        if (double.IsNaN(d))
        {
            return MaskedZeroReason.NotANumber;
        }

        return double.IsInfinity(d) ? MaskedZeroReason.Infinity : MaskedZeroReason.None;
    }
}
