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

    /// <summary>
    /// Вихід пишеться на кожну речовину (<c>true</c>) чи раз на рядок без
    /// речовини (<c>false</c>) — <c>D-176</c>, V-7.
    /// </summary>
    /// <remarks>
    /// ⛔ Типове — <c>true</c>: так рушій пише кожен вихід сьогодні. Інакше
    /// Row-величини на кшталт <c>M_t</c> лягли б у результати N разів, по
    /// одному на речовину, і сума по них дала б N-кратне число.
    /// </remarks>
    public bool IsPerSubstance { get; private set; } = true;

    /// <summary>Оголошує, чи вихід пишеться на кожну речовину.</summary>
    /// <param name="isPerSubstance"><c>false</c> — раз на рядок, без речовини.</param>
    public void SetPerSubstance(bool isPerSubstance) => IsPerSubstance = isPerSubstance;

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
