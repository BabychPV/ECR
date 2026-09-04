using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Рядок фіксованої таблиці. Ідентичність — <see cref="RowKey"/>;
/// <see cref="Ordinal"/> відповідає лише за порядок на екрані.
/// </summary>
/// <remarks>
/// Це головна відмінність від чинного рішення, де ідентичність рядка була
/// позиційною (<c>Attribute_0010</c> = «перший рядок діапазону»), і вставка
/// рядка в Excel робила всі раніше збережені дані неправильними.
/// </remarks>
public sealed class RowDef : Entity<int>
{
    private RowDef() { }

    public RowDef(int tableDefId, RowKey rowKey, int ordinal, LocalizedText label, RowKind rowKind)
    {
        TableDefId = tableDefId;
        RowKeyValue = rowKey.Value;
        Ordinal = ordinal;
        LabelL10n = label;
        RowKind = rowKind;
    }

    public int TableDefId { get; private set; }

    /// <summary>Стабільна бізнес-ідентичність (<c>"7001001"</c>).</summary>
    public string RowKeyValue { get; private set; } = null!;

    /// <summary>Порядок відображення. Змінюється після публікації — це презентаційна правка.</summary>
    public int Ordinal { get; private set; }

    public LocalizedText LabelL10n { get; private set; } = null!;
    public RowKind RowKind { get; private set; }
    public int? ParentRowDefId { get; private set; }
    public bool IsReadOnly { get; private set; }
    public int? StyleId { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    /// <summary>Змінює порядок — презентаційна операція.</summary>
    public void Reorder(int ordinal) => Ordinal = ordinal;
}
