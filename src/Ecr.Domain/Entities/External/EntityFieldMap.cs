// src/Ecr.Domain/Entities/External/EntityFieldMap.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.External;

/// <summary>Куди лягає поле джерела.</summary>
public enum FieldTargetKind : byte
{
    /// <summary>Колонка таблиці документа.</summary>
    Column = 0,

    /// <summary>Поле запису довідника.</summary>
    RegistryField = 1,
}

/// <summary>
/// Мапінг поля джерела на поле ECR (<c>ext.EntityFieldMap</c>).
/// </summary>
/// <remarks>
/// ⚠ Одиниці **на межі інтеграції**: атрибути PI AF мають власний UOM, і це
/// найчастіше джерело мовчазних розбіжностей у числах (ФВ-16.9). Тому
/// <see cref="SourceUnitId"/> і <see cref="TargetUnitId"/> зберігаються обидва,
/// а конверсія виконується при завантаженні з журналом — інакше повторний
/// перерахунок з архіву дасть інший результат (ФВ-16.10, D-79).
/// </remarks>
public sealed class EntityFieldMap : Entity<int>
{
    private EntityFieldMap() { }

    private EntityFieldMap(int sourceEntityId, string sourceField)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceField);

        SourceEntityId = sourceEntityId;
        SourceField = sourceField;
        IsActive = true;
    }

    /// <summary>Мапінг на колонку документа.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourceField">Поле в джерелі.</param>
    /// <param name="columnDefId">Колонка-ціль.</param>
    public static EntityFieldMap ToColumn(int sourceEntityId, string sourceField, int columnDefId)
        => new(sourceEntityId, sourceField)
        {
            TargetKind = FieldTargetKind.Column,
            TargetColumnDefId = columnDefId,
        };

    /// <summary>Мапінг на поле довідника.</summary>
    /// <param name="sourceEntityId">Сутність джерела.</param>
    /// <param name="sourceField">Поле в джерелі.</param>
    /// <param name="registryFieldDefId">Поле довідника-ціль.</param>
    public static EntityFieldMap ToRegistryField(
        int sourceEntityId, string sourceField, int registryFieldDefId)
        => new(sourceEntityId, sourceField)
        {
            TargetKind = FieldTargetKind.RegistryField,
            TargetRegistryFieldDefId = registryFieldDefId,
        };

    public int SourceEntityId { get; private set; }
    public string SourceField { get; private set; } = null!;

    /// <summary>Одиниця ДЖЕРЕЛА; її зміна зупиняє збір (ФВ-16.9).</summary>
    public int? SourceUnitId { get; private set; }

    public FieldTargetKind TargetKind { get; private set; }
    public int? TargetColumnDefId { get; private set; }
    public int? TargetRegistryFieldDefId { get; private set; }

    /// <summary>Одиниця, у якій значення лягає в ECR.</summary>
    public int? TargetUnitId { get; private set; }

    /// <summary>Іменоване перетворення зі списку; довільний код заборонений.</summary>
    public string? TransformCode { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Ставить одиниці межі.</summary>
    /// <param name="sourceUnitId">Одиниця джерела.</param>
    /// <param name="targetUnitId">Одиниця цілі.</param>
    public void SetUnits(int? sourceUnitId, int? targetUnitId)
    {
        SourceUnitId = sourceUnitId;
        TargetUnitId = targetUnitId;
    }

    /// <summary>Ставить іменоване перетворення.</summary>
    /// <param name="transformCode">Код перетворення; <c>null</c> — без нього.</param>
    public void SetTransform(string? transformCode) => TransformCode = transformCode;

    /// <summary>Вимикає мапінг.</summary>
    public void Deactivate() => IsActive = false;
}
