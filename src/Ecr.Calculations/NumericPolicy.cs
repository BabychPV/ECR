using Ecr.Domain.Enums;

namespace Ecr.Calculations;

/// <summary>
/// Арифметична політика версії методології.
/// </summary>
/// <remarks>
/// Режим <see cref="NumericMode.Legacy"/> існує **виключно** заради побітової
/// сумісності з числами чинної системи (ФВ-9.9): порядок операцій, момент
/// округлення і кількість знаків мають збігатися. Це не «гірший» режим —
/// це умова того, що звірка з еталоном узагалі можлива.
/// </remarks>
public sealed class NumericPolicy(NumericMode mode)
{
    /// <summary>Режим.</summary>
    public NumericMode Mode { get; } = mode;

    /// <summary>Округлення згідно з режимом.</summary>
    /// <param name="value">Значення.</param>
    /// <param name="digits">Скільки знаків лишити.</param>
    /// <remarks>
    /// ⚠ <see cref="MidpointRounding.AwayFromZero"/> в **обох** режимах.
    /// Банківське округлення (<c>ToEven</c>, типове для .NET) дало б інші
    /// числа на кожному «.5», і звірка з чинною системою розійшлася б у
    /// сотнях рядків без жодної помилки у формулі.
    /// </remarks>
    public static decimal Round(decimal value, int digits)
        => decimal.Round(value, digits, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Округлення проміжного кроку — те, що відрізняє режими.
    /// </summary>
    /// <param name="value">Значення кроку.</param>
    /// <remarks>
    /// ⛔ Ось уся різниця <c>Legacy</c> і <c>Strict</c>: **момент** округлення,
    /// а не спосіб. <c>Legacy</c> округлює після кожного кроку так само, як
    /// чинна система на аркуші Excel; <c>Strict</c> веде повну точність і
    /// округлює лише на виході. На ланцюгу з чотирьох формул різниця
    /// накопичується до шостого знака — рівно там, де йде звірка.
    /// </remarks>
    public decimal RoundStep(decimal value)
        => Mode == NumericMode.Legacy ? Round(value, OutputScale) : value;

    /// <summary>Округлення значення, що йде в <c>calc.CalculationResult</c>.</summary>
    /// <param name="value">Значення виходу.</param>
    public decimal RoundOutput(decimal value) => Round(value, OutputScale);

    /// <summary>Скільки знаків зберігати для виходу.</summary>
    public int OutputScale => 6;
}
