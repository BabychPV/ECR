// src/Ecr.Domain/Entities/Calculations/MethodologySubstance.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Речовина, яку рахує версія методології. Сама речовина — запис довідника;
/// тут лише прив'язка і параметри, специфічні для цього розрахунку.
/// </summary>
public sealed class MethodologySubstance : Entity<int>
{
    private MethodologySubstance() { }

    public MethodologySubstance(int methodologyVersionId, long substanceEntryId, int ordinal = 0)
    {
        MethodologyVersionId = methodologyVersionId;
        SubstanceEntryId = substanceEntryId;
        Ordinal = ordinal;
    }

    public int MethodologyVersionId { get; private set; }

    /// <summary>Посилання на `dic.RegistryEntry`, а не текстова назва (ФВ-8.8).</summary>
    public long SubstanceEntryId { get; private set; }

    /// <summary>Порядок у переліку речовин методології.</summary>
    /// <remarks>
    /// ⚠ Прапорця <c>IsActive</c> тут НЕМАЄ — і це не пропуск (`calc`-частина
    /// `Q-027`). Речовина або входить у версію, або ні: «вимкнена» речовина
    /// означала б, що версія рахує не те, що в ній записано, і зріз подання
    /// перестав би пояснювати власні числа. Прибрати речовину — це нова версія.
    /// </remarks>
    public int Ordinal { get; private set; }
}
