using Ecr.Domain.Enums;

namespace Ecr.Expressions.Evaluation;

/// <summary>
/// Єдина відповідність «режим версії → арифметика».
/// </summary>
/// <remarks>
/// ⛔ Точка задання **одна**, і живе вона поруч із двома реалізаціями, а не в
/// тому, хто їх обирає. Доти вибір робив <c>NumericPolicy</c> у
/// <c>Ecr.Calculations</c>; щойно арифметика знадобилася ще й обчислювачеві,
/// з'явилася б друга така умова — і розійтися вони могли б рівно там, де
/// різницю видно лише як інші числа.
/// </remarks>
public static class EvaluationArithmetics
{
    /// <summary>Арифметика режиму.</summary>
    /// <param name="mode">Режим версії методології.</param>
    /// <returns>Реалізація, якою рахуються оператори і функції.</returns>
    /// <remarks>
    /// ⚠ Обидві реалізації без стану, тож створюються на вимогу і не
    /// кешуються: спільний екземпляр не дав би нічого, крім ще одного
    /// статичного поля.
    /// </remarks>
    public static IEvaluationArithmetic For(NumericMode mode)
        => mode == NumericMode.Legacy
            ? new LegacyDoubleArithmetic()
            : new StrictDecimalArithmetic();
}
