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

    public MethodologyOutput(int methodologyVersionId, EcrCode code, int unitId)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code.Value;
        UnitId = unitId;
    }

    public int MethodologyVersionId { get; private set; }
    public string Code { get; private set; } = null!;

    /// <summary>Одиниця результату. Несумісна з формулою → відмова публікації.</summary>
    public int UnitId { get; private set; }

    /// <summary>Формула, що дає цей вихід.</summary>
    public int? MethodologyFormulaId { get; private set; }
}
