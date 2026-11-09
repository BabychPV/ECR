namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Feature (j): one PART of a month value of a row whose permit changed in that month (<c>doc.CellPermitPart</c>) — the
/// value the user typed for one permit (human 08.11: «Користувач має сам поділити значення для місяця на 2 частини»;
/// never pro-rata). The month cell keeps the TOTAL (sum of the parts), so every existing reader stays correct and nothing
/// is counted twice; the parts say which permit owns how much (limits, reports, views by permit).
/// </summary>
/// <remarks>
/// ⚠ Partitioned like <c>doc.CellValue</c> (<c>ps_ByPeriodKey</c>, <c>07-partition-tables.sql</c>): <see cref="PeriodKeyValue"/>
/// is the month of the part (<c>YYYYMM</c>). The cell is addressed LOGICALLY (table, sheet copy, row key, month column),
/// as <see cref="PermitSegment"/>, so the parts do not depend on Q-17 month routing.
/// </remarks>
public sealed class CellPermitPart
{
    private CellPermitPart() { }

    /// <summary>A part of a month value.</summary>
    public CellPermitPart(
        int periodKey, long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId, byte partNo,
        long registryEntryId, DateOnly dayFrom, DateOnly dayTo, decimal? valueNumeric)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);
        if (dayTo < dayFrom)
        {
            throw new ArgumentOutOfRangeException(nameof(dayTo), dayTo, "The part ends before it starts.");
        }

        PeriodKeyValue = periodKey;
        DocumentId = documentId;
        TableDefId = tableDefId;
        SheetCopyId = sheetCopyId;
        RowKey = rowKey;
        ColumnDefId = columnDefId;
        PartNo = partNo;
        RegistryEntryId = registryEntryId;
        DayFrom = dayFrom;
        DayTo = dayTo;
        ValueNumeric = valueNumeric;
    }

    /// <summary>Surrogate key (with <see cref="PeriodKeyValue"/> — aligned with the partition).</summary>
    public long Id { get; private set; }

    /// <summary>Month of the part (<c>YYYYMM</c>), partition key.</summary>
    public int PeriodKeyValue { get; private set; }

    /// <summary>Document.</summary>
    public long DocumentId { get; private set; }

    /// <summary>Table.</summary>
    public int TableDefId { get; private set; }

    /// <summary>Sheet copy (RS); <c>null</c> — the original sheet.</summary>
    public long? SheetCopyId { get; private set; }

    /// <summary>Row key.</summary>
    public string RowKey { get; private set; } = string.Empty;

    /// <summary>The month column.</summary>
    public int ColumnDefId { get; private set; }

    /// <summary>1 = part A, 2 = part B, … in date order.</summary>
    public byte PartNo { get; private set; }

    /// <summary>The permit of this part.</summary>
    public long RegistryEntryId { get; private set; }

    /// <summary>First day of the part.</summary>
    public DateOnly DayFrom { get; private set; }

    /// <summary>Last day of the part (inclusive).</summary>
    public DateOnly DayTo { get; private set; }

    /// <summary>The value the user typed; <c>null</c> — not entered yet (partial bulk save).</summary>
    public decimal? ValueNumeric { get; private set; }

    /// <summary>Replaces the value.</summary>
    public void SetValue(decimal? value) => ValueNumeric = value;
}
