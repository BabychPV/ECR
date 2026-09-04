// src/Ecr.Domain/Entities/External/EntityFieldMap.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.External;

/// <summary>
/// Мапінг поля джерела на наше поле, разом із **одиницями обох боків**
/// (ФВ-16.10, D-79).
/// </summary>
/// <remarks>
/// ⚠ Сирі дані в <c>ext.*</c> зберігаються **в одиниці джерела**, конверсія
/// відбувається при завантаженні і потрапляє в журнал. Інакше повторний
/// перерахунок з архіву дав би інший результат, ніж перший.
/// <para>
/// Зміна одиниці атрибута в джерелі **зупиняє збір** (ФВ-16.9,
/// <c>ECR-INT-0422</c>) — не конвертує «як здається». Це найчастіше джерело
/// мовчазних розбіжностей у числах.
/// </para>
/// </remarks>
public sealed class EntityFieldMap : Entity<int>
{
    private EntityFieldMap() { }

    public EntityFieldMap(int sourceEntityId, string sourceField, string targetField)
    {
        SourceEntityId = sourceEntityId;
        SourceField = sourceField;
        TargetField = targetField;
        IsActive = true;
    }

    public int SourceEntityId { get; private set; }
    public string SourceField { get; private set; } = null!;
    public string TargetField { get; private set; } = null!;

    /// <summary>Одиниця в джерелі, як її оголосив постачальник даних.</summary>
    public int? SourceUnitId { get; private set; }

    /// <summary>Одиниця, в якій значення потрібне нам.</summary>
    public int? TargetUnitId { get; private set; }

    public bool IsActive { get; private set; }
}
