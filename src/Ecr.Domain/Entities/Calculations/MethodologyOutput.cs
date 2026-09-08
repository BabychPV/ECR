// src/Ecr.Domain/Entities/Calculations/MethodologyOutput.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Оголошений вихід методології: що саме вона повертає і **в якій одиниці**.
/// Одиниця обов'язкова — на ній тримається перевірка розмірностей при
/// публікації (ФВ-16.6).
/// </summary>
public sealed class MethodologyOutput : Entity<int>
{
    private MethodologyOutput() { }

    public MethodologyOutput(int methodologyVersionId, EcrCode code, int unitId, int ordinal = 0)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code.Value;
        UnitId = unitId;
        Ordinal = ordinal;
    }

    public int MethodologyVersionId { get; private set; }
    public string Code { get; private set; } = null!;

    /// <summary>Одиниця результату. Несумісна з формулою → відмова публікації.</summary>
    public int UnitId { get; private set; }

    /// <summary>Порядок у переліку виходів.</summary>
    /// <remarks>
    /// ⚠ Посилання на формулу тут НЕМАЄ (`calc`-частина `Q-027`): вихід
    /// зв'язується з формулою за <see cref="Code"/>, як і всі інші посилання
    /// в діалекті методологій. Числовий ключ додав би другий спосіб сказати те
    /// саме — і місце, де вони розійдуться.
    /// </remarks>
    public int Ordinal { get; private set; }

    /// <summary>Переписує одиницю й порядок наявного виходу.</summary>
    /// <param name="unitId">Нова одиниця результату.</param>
    /// <param name="ordinal">Новий порядок у переліку.</param>
    /// <remarks>
    /// ⛔ Метод <c>internal</c>: єдиний вхід — <see cref="MethodologyVersion.EditOutput"/>,
    /// бо вихід не знає, опублікована його версія чи ні. Зміна одиниці в
    /// опублікованій версії не змінює жодного числа в базі і змінює ЗНАЧЕННЯ
    /// кожного з них: ті самі 12.5 стають тоннами замість кілограмів.
    ///
    /// ⚠ <see cref="Code"/> не змінюється: він і є адресою виходу — на нього
    /// посилаються <c>cfg.CalculationBinding.OutputCode</c> і очікування тестів.
    /// </remarks>
    internal void Update(int unitId, int ordinal)
    {
        UnitId = unitId;
        Ordinal = ordinal;
    }
}
