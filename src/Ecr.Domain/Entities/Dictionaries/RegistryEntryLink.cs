// src/Ecr.Domain/Entities/Dictionaries/RegistryEntryLink.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Зв'язок M:N між записами довідників (ФВ-8.4). <see cref="PayloadJson"/>
/// тримає атрибути самого зв'язку — наприклад частку або пріоритет, — щоб не
/// заводити окрему сутність під кожен вид відношення.
/// </summary>
public sealed class RegistryEntryLink : Entity<long>
{
    private RegistryEntryLink() { }

    public RegistryEntryLink(int relationDefId, long fromEntryId, long toEntryId)
    {
        RegistryRelationDefId = relationDefId;
        FromEntryId = fromEntryId;
        ToEntryId = toEntryId;
    }

    public int RegistryRelationDefId { get; private set; }
    public long FromEntryId { get; private set; }
    public long ToEntryId { get; private set; }

    /// <summary>Атрибути зв'язку. Схема — за `RegistryRelationDef`.</summary>
    public string? PayloadJson { get; private set; }

    public void SetPayload(string? json)
        => throw new NotImplementedException(
            "TODO: валідувати JSON за схемою з RegistryRelationDef; невалідний — " +
            "помилка, а не мовчазне збереження рядка.");
}
