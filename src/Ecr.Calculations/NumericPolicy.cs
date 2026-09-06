using Ecr.Domain.Enums;
using Ecr.Expressions.Evaluation;

namespace Ecr.Calculations;

/// <summary>
/// Арифметична політика версії методології.
/// </summary>
/// <remarks>
/// ⛔ **Виправлено за директивою №05 §5–§6.** Тут стояло, що режими різняться
/// **моментом** округлення: нібито <c>Legacy</c> округлює після кожної
/// операції, а <c>Strict</c> — на виході. Це було вигадано: обидва рахували в
/// <c>decimal</c> і обидва округлювали <c>AwayFromZero</c>.
///
/// Наслідок був би найтихішим із можливих — <c>Legacy</c> рахував би
/// **краще** за чинну систему, і звірка розійшлася б у шостому знаку на
/// кожній формулі без жодної помилки у формулі.
///
/// ⚠ Справжня різниця — **тип і правило**, а не момент:
///
/// | Режим | Арифметика | Округлення |
/// |---|---|---|
/// | <c>Legacy</c> | <c>double</c>, семантика NCalc 1.3.8 | банківське (<c>ToEven</c>) |
/// | <c>Strict</c> | наскрізний <c>decimal</c> | від нуля (<c>AwayFromZero</c>) |
///
/// ⛔ Точок округлення в чинному CLR рівно дві, і жодна з них не «після кожної
/// операції»: явний виклик <c>Round()</c> у тексті формули і **збереження
/// проміжного результату** між залежними методологіями, де число проходить
/// через колонку і втрачає знаки за її типом. Друге означає, що ребра графа
/// <c>calc.MethodologyDependency</c> — це точки округлення, і наскрізний
/// розрахунок «у пам'яті без матеріалізації» дав би інші числа навіть за
/// ідентичної арифметики.
/// </remarks>
/// <param name="mode">Режим версії методології.</param>
public sealed class NumericPolicy(NumericMode mode)
{
    /// <summary>Режим.</summary>
    public NumericMode Mode { get; } = mode;

    /// <summary>
    /// Арифметика цього режиму.
    /// </summary>
    /// <remarks>
    /// ⚠ Обидві реалізації без стану, тож створюються тут і не кешуються:
    /// спільний екземпляр не дав би нічого, крім ще одного статичного поля.
    /// </remarks>
    public IEvaluationArithmetic Arithmetic { get; } = mode == NumericMode.Legacy
        ? new LegacyDoubleArithmetic()
        : new StrictDecimalArithmetic();

    /// <summary>
    /// Округлення значення, що йде в <c>calc.CalculationResult</c>.
    /// </summary>
    /// <param name="value">Значення виходу.</param>
    /// <remarks>
    /// ⛔ Правило береться з <see cref="Arithmetic"/>, а не задається тут:
    /// друга точка задання розійшлася б із першою, і розбіжність була б видима
    /// лише як інше число на кожному «.5».
    /// </remarks>
    public decimal RoundOutput(decimal value)
        => decimal.Round(value, OutputScale, Midpoint(Arithmetic.Rounding));

    /// <summary>
    /// Скільки знаків зберігати для виходу методології.
    /// </summary>
    /// <remarks>
    /// ⚠ Шість — це **подання** (<c>ФВ-9.16a</c>). Конвеєр чинної системи несе
    /// шістнадцять: проміжні колонки газового складу оголошені
    /// <c>decimal(25,16)</c>, і саме там лежить точка округлення між кроками.
    /// </remarks>
    public int OutputScale => 6;

    /// <summary>Правило округлення режиму в термінах BCL.</summary>
    /// <param name="rounding">Правило.</param>
    public static MidpointRounding Midpoint(RoundingMode rounding)
        => rounding == RoundingMode.ToEven
            ? MidpointRounding.ToEven
            : MidpointRounding.AwayFromZero;
}
