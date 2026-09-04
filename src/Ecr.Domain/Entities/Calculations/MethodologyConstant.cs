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

    /// <summary>
    /// Категорія застосування: <c>default</c>, <c>offshore</c>, назва
    /// установки. <c>null</c> — константа спільна для всіх.
    /// </summary>
    /// <remarks>
    /// ⚠ Разом із <see cref="SubstanceEntryId"/> утворює ключ звуження:
    /// точний збіг виграє над загальним. Без категорії довелося б заводити
    /// окрему методологію на кожну установку — саме те, від чого система
    /// відходить (`calc`-частина `Q-027`).
    /// </remarks>
    public string? Category { get; private set; }

    /// <summary>Звідки взято значення: наказ, паспорт установки, вимірювання.</summary>
    /// <remarks>
    /// Не метадані «для порядку»: коефіцієнт емісії без джерела неможливо ні
    /// захистити перед регулятором, ні оновити, коли документ перевидадуть.
    /// </remarks>
    public string? Source { get; private set; }

    /// <summary>Звужує застосування константи.</summary>
    /// <param name="category">Категорія; <c>null</c> — спільна.</param>
    /// <param name="substanceEntryId">Речовина; <c>null</c> — спільна.</param>
    public void SetScope(string? category, long? substanceEntryId)
    {
        Category = category;
        SubstanceEntryId = substanceEntryId;
    }

    /// <summary>Задає вікно чинності.</summary>
    /// <param name="from">Початок; <c>null</c> — від початку.</param>
    /// <param name="to">Кінець; <c>null</c> — без обмеження.</param>
    /// <exception cref="DomainException">Порожнє вікно — <c>ECR-CALC-0422</c>.</exception>
    public void SetValidity(DateOnly? from, DateOnly? to)
    {
        if (from is { } start && to is { } end && end < start)
        {
            throw new DomainException(
                "ECR-CALC-0422", $"Кінець вікна чинності {end} раніший за початок {start}.");
        }

        ValidFrom = from;
        ValidTo = to;
    }

    /// <summary>Записує джерело значення.</summary>
    /// <param name="source">Опис джерела.</param>
    public void SetSource(string? source) => Source = source;
}
