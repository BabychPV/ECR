// src/Ecr.Domain/Entities/Dictionaries/RegistryValue.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Значення одного поля запису довідника. Колонки **типізовані**, а не одне
/// текстове поле: інакше повернулася б та сама проблема, що з
/// <c>Attribute_XXXX</c> — тип відомий лише за домовленістю.
/// </summary>
public sealed class RegistryValue : Entity<long>
{
    private RegistryValue() { }

    public RegistryValue(long registryEntryId, int registryFieldDefId)
    {
        RegistryEntryId = registryEntryId;
        RegistryFieldDefId = registryFieldDefId;
    }

    public long RegistryEntryId { get; private set; }
    public int RegistryFieldDefId { get; private set; }

    public decimal? ValueDecimal { get; private set; }
    public string? ValueString { get; private set; }
    public DateTime? ValueDate { get; private set; }
    public bool? ValueBool { get; private set; }
    public long? ValueRegistryEntryId { get; private set; }

    /// <summary>Одиниця значення, якщо поле її має (ФВ-16.1).</summary>
    public int? UnitId { get; private set; }

    /// <summary>Записує значення відповідно до типу поля.</summary>
    public void Set(object? value, int? unitId)
        => throw new NotImplementedException(
            "TODO: обрати колонку за RegistryFieldDef.DataType; решту занулити — " +
            "заповнені дві колонки означають, що тип змінили і не прибрали старе. " +
            "unitId приймати лише для полів з оголошеною розмірністю.");
}
