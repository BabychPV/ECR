using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Значення поля шапки документа. Рівно один запис на пару
/// <c>(DocumentId, HeaderFieldDefId)</c> — на відміну від <see cref="CellValue"/>
/// тут немає ні періоду, ні рядка таблиці: шапка належить документу цілком.
/// </summary>
public sealed class DocumentHeaderValue
{
    private DocumentHeaderValue() { }

    public DocumentHeaderValue(long documentId, int headerFieldDefId, DocumentHeaderValueData data)
    {
        DocumentId = documentId;
        HeaderFieldDefId = headerFieldDefId;
        Apply(data);
    }

    public long DocumentId { get; private set; }
    public int HeaderFieldDefId { get; private set; }

    public string? ValueString { get; private set; }
    public decimal? ValueNumeric { get; private set; }
    public DateTime? ValueDate { get; private set; }
    public bool? ValueBool { get; private set; }
    public long? ValueRegistryEntryId { get; private set; }
    public int? ValueUnitId { get; private set; }
    public bool IsEmpty { get; private set; }

    /// <summary>Поточне значення як значеннєвий тип.</summary>
    public DocumentHeaderValueData ToData() => new()
    {
        ValueString = ValueString,
        ValueNumeric = ValueNumeric,
        ValueDate = ValueDate,
        ValueBool = ValueBool,
        ValueRegistryEntryId = ValueRegistryEntryId,
        ValueUnitId = ValueUnitId,
        IsEmpty = IsEmpty,
    };

    /// <summary>Замінює значення.</summary>
    public void Apply(DocumentHeaderValueData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValueString = data.ValueString;
        ValueNumeric = data.ValueNumeric;
        ValueDate = data.ValueDate;
        ValueBool = data.ValueBool;
        ValueRegistryEntryId = data.ValueRegistryEntryId;
        ValueUnitId = data.ValueUnitId;
        IsEmpty = data.IsEmpty;
    }
}
