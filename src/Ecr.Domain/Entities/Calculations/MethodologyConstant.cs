// src/Ecr.Domain/Entities/Calculations/MethodologyConstant.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Константа методології: щільність, теплотворність, молярна маса, коефіцієнт
/// емісії. **Одиниця обов'язкова** (ФВ-16.1).
/// </summary>
/// <remarks>
/// ⚠ Саме тут живуть **контекстні коефіцієнти**, а не в <c>uom.Conversion</c>
/// (ФВ-16.5, D-75). Щільність води — не конверсія «м³ → кг»: вона залежить від
/// температури, а для нафти взагалі інша. Спроба покласти таке в таблицю
/// конверсій відхиляється базою (<c>ECR-UOM-4221</c>).
/// </remarks>
public sealed class MethodologyConstant : Entity<int>
{
    private MethodologyConstant() { }

    public MethodologyConstant(int methodologyVersionId, EcrCode code, decimal value, int unitId)
    {
        MethodologyVersionId = methodologyVersionId;
        Code = code.Value;
        Value = value;
        UnitId = unitId;
    }

    public int MethodologyVersionId { get; private set; }
    public string Code { get; private set; } = null!;

    /// <summary>Зберігається типізовано (<c>decimal</c>), не текстом.</summary>
    public decimal Value { get; private set; }

    /// <summary>Одиниця. Без неї константа не має сенсу в перевірці розмірностей.</summary>
    public int UnitId { get; private set; }

    public DateOnly? ValidFrom { get; private set; }
    public DateOnly? ValidTo { get; private set; }
    public long? SubstanceEntryId { get; private set; }
}
