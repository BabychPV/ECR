// src/Ecr.Domain/Entities/Calculations/MethodologyVersion.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Calculations;

/// <summary>
/// Версія методології — те, що реально рахує. Опублікована **незмінна**;
/// зміна — клон плюс нове вікно дії.
/// </summary>
/// <remarks>
/// Три режими тут визначають числа, і жоден не є технічною дрібницею:
/// <see cref="NumericMode"/> — момент округлення (ФВ-9.9),
/// <see cref="CalendarMode"/> — тривалість періоду (ФВ-16.11),
/// <see cref="TraceLevel"/> — обсяг журналу (ФВ-9.13).
/// Перші два **обов'язкові в diff при публікації**: їх зміна тихо змінює
/// всі результати.
/// </remarks>
public sealed class MethodologyVersion : Entity<int>
{
    private MethodologyVersion() { }

    public MethodologyVersion(int methodologyId, string versionNumber, DateOnly effectiveFrom)
    {
        MethodologyId = methodologyId;
        VersionNumber = versionNumber;
        EffectiveFrom = effectiveFrom;
        NumericMode = NumericMode.Legacy;
        CalendarMode = CalendarMode.Actual;
        TraceLevel = TraceLevel.ErrorsOnly;
        Status = TemplateVersionStatus.Draft;
    }

    public int MethodologyId { get; private set; }
    public string VersionNumber { get; private set; } = null!;
    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly? EffectiveTo { get; private set; }
    public TemplateVersionStatus Status { get; private set; }

    /// <summary>Арифметика: <c>Legacy</c> відтворює числа чинної системи (ФВ-9.9).</summary>
    public NumericMode NumericMode { get; private set; }

    /// <summary>Джерело тривалості періоду (ФВ-16.11). Не зберігається на періоді (D-112).</summary>
    public CalendarMode CalendarMode { get; private set; }

    public TraceLevel TraceLevel { get; private set; }

    public int? LastEditedByUserId { get; private set; }
    public int? PublishedByUserId { get; private set; }
    public DateTime? PublishedAt { get; private set; }
    public string? ChangeReason { get; private set; }

    /// <summary>
    /// Публікація. **Чотири очі** (D-40): публікувати власну останню правку
    /// заборонено системно, а не інструкцією.
    /// </summary>
    public void Publish(int publishedByUserId, string changeReason, DateTime utcNow)
        => throw new NotImplementedException(
            "TODO: 1) Status має бути Draft;\n" +
            "2) publishedByUserId != LastEditedByUserId, інакше ECR-CALC-0409 (D-40);\n" +
            "3) changeReason обов'язковий і непорожній (ФВ-14.7);\n" +
            "4) вікно дії не перетинається з іншими опублікованими версіями (ФВ-13.3);\n" +
            "5) зелений тест обов'язковий, інакше ECR-CALC-0422 (ФВ-9.12);\n" +
            "6) Status = Published, зафіксувати автора і час. " +
            "Перевірку diff результатів робить use-case: сутність не має доступу до даних.");
}
