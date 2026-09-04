using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Значення комірки. Основний обсяг системи — ~108 млн рядків на рік.
/// </summary>
/// <remarks>
/// Порожні комірки **не матеріалізуються**: рядок існує тільки для заповненого
/// значення або для явної порожнечі (<c>IsEmpty = 1</c>). Це два різні стани,
/// і зводити їх один до одного не можна (R-B4).
/// </remarks>
public sealed class CellValue
{
    private CellValue() { }

    public CellValue(CellAddress address, int tableDefId, CellValueData data)
    {
        PeriodKeyValue = address.PeriodKey.Value;
        TableRowId = address.TableRowId;
        ColumnDefId = address.ColumnDefId;
        TableDefId = tableDefId;
        Apply(data);
    }

    public int PeriodKeyValue { get; private set; }
    public long TableRowId { get; private set; }
    public int ColumnDefId { get; private set; }

    /// <summary>Денормалізовано під складений FK: комірка не може потрапити в чужу колонку.</summary>
    public int TableDefId { get; private set; }

    public string? ValueString { get; private set; }
    public decimal? ValueNumeric { get; private set; }
    public DateTime? ValueDate { get; private set; }
    public bool? ValueBool { get; private set; }
    public int? ValueRegistryEntryId { get; private set; }

    /// <summary>Одиниця на рядок; лише для <c>DataType = Unit</c> (R-A4).</summary>
    public int? ValueUnitId { get; private set; }

    public bool IsCalculated { get; private set; }
    public bool IsEmpty { get; private set; }

    public CellAddress Address => new(new PeriodKey(PeriodKeyValue), TableRowId, ColumnDefId);

    /// <summary>Поточне значення як значеннєвий тип.</summary>
    public CellValueData ToData() => new()
    {
        ValueString = ValueString,
        ValueNumeric = ValueNumeric,
        ValueDate = ValueDate,
        ValueBool = ValueBool,
        ValueRegistryEntryId = ValueRegistryEntryId,
        ValueUnitId = ValueUnitId,
        IsCalculated = IsCalculated,
        IsEmpty = IsEmpty
    };

    /// <summary>Замінює значення.</summary>
    public void Apply(CellValueData data)
    {
        ValueString = data.ValueString;
        ValueNumeric = data.ValueNumeric;
        ValueDate = data.ValueDate;
        ValueBool = data.ValueBool;
        ValueRegistryEntryId = data.ValueRegistryEntryId;
        ValueUnitId = data.ValueUnitId;
        IsCalculated = data.IsCalculated;
        IsEmpty = data.IsEmpty;
    }
}
