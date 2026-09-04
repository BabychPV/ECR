using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>Рядок таблиці документа.</summary>
/// <remarks>
/// <see cref="ModifiedAt"/> оновлюється **завжди** при зміні комірок цього
/// рядка — саме це піднімає <c>RowVersion</c>. Забути про це = зламати
/// оптимістичне блокування тихо (B04 §2.4).
/// </remarks>
public sealed class TableRow
{
    private TableRow() { }

    public TableRow(PeriodKey periodKey, long id, long tableInstanceId, RowKey rowKey, int ordinal, DateTime utcNow)
    {
        PeriodKeyValue = periodKey.Value;
        Id = id;
        TableInstanceId = tableInstanceId;
        RowKeyValue = rowKey.Value;
        Ordinal = ordinal;
        ModifiedAt = utcNow;
    }

    public int PeriodKeyValue { get; private set; }
    public long Id { get; private set; }
    public long TableInstanceId { get; private set; }
    public string RowKeyValue { get; private set; } = null!;

    /// <summary>Заповнене для <c>RowMode = Fixed</c>.</summary>
    public int? RowDefId { get; private set; }

    public int Ordinal { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime ModifiedAt { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    public PeriodKey PeriodKey => new(PeriodKeyValue);
    public RowKey RowKey => ValueObjects.RowKey.Create(RowKeyValue);

    /// <summary>«Дотик» рядка при зміні його комірок.</summary>
    public void Touch(DateTime utcNow) => ModifiedAt = utcNow;

    /// <summary>Логічне видалення рядка динамічної таблиці.</summary>
    public void SoftDelete(DateTime utcNow)
    {
        IsDeleted = true;
        ModifiedAt = utcNow;
    }
}
