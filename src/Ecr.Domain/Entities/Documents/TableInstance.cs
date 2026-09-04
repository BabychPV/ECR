using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Екземпляр таблиці документа за період. Ключ складений
/// <c>(PeriodKey, Id)</c>: партиційний ключ мусить входити в PK, інакше
/// індекси не вирівняні (R-A1).
/// </summary>
public sealed class TableInstance
{
    private TableInstance() { }

    public TableInstance(PeriodKey periodKey, long id, long documentId, int tableDefId, DateTime utcNow)
    {
        PeriodKeyValue = periodKey.Value;
        Id = id;
        DocumentId = documentId;
        TableDefId = tableDefId;
        CreatedAt = utcNow;
        ModifiedAt = utcNow;
    }

    public int PeriodKeyValue { get; private set; }

    /// <summary>Береться з <c>SEQUENCE</c>, не <c>IDENTITY</c>: значення потрібне до вставки.</summary>
    public long Id { get; private set; }

    public long DocumentId { get; private set; }
    public int TableDefId { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime ModifiedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public PeriodKey PeriodKey => new(PeriodKeyValue);

    public void Touch(DateTime utcNow) => ModifiedAt = utcNow;
}
