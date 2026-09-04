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
    public decimal Round(decimal value, int digits)
        => throw new NotImplementedException(
            "TODO: MidpointRounding.AwayFromZero в обох режимах — банківське округлення дало б " +
            "інші числа. Різниця Legacy/Strict — у МОМЕНТІ округлення: Legacy округлює після " +
            "кожного кроку так само, як чинна система, Strict — лише на виході.");

    /// <summary>Скільки знаків зберігати для виходу.</summary>
    public int OutputScale => 6;
}
