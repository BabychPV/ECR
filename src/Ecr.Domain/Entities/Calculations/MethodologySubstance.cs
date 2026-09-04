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

    public MethodologySubstance(int methodologyVersionId, long substanceEntryId)
    {
        MethodologyVersionId = methodologyVersionId;
        SubstanceEntryId = substanceEntryId;
    }

    public int MethodologyVersionId { get; private set; }

    /// <summary>Посилання на `dic.RegistryEntry`, а не текстова назва (ФВ-8.8).</summary>
    public long SubstanceEntryId { get; private set; }

    public int Ordinal { get; private set; }
    public bool IsActive { get; private set; } = true;
}
