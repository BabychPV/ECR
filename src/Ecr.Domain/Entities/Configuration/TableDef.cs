using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Таблиця на аркуші.</summary>
public sealed class TableDef : Entity<int>
{
    private readonly List<ColumnDef> _columns = [];
    private readonly List<RowDef> _rows = [];

    private TableDef() { }

    public TableDef(int sheetDefId, EcrCode code, LocalizedText name, int ordinal,
                    TableLayoutKind layoutKind, TableRowMode rowMode)
    {
        SheetDefId = sheetDefId;
        Code = code.Value;
        NameL10n = name;
        Ordinal = ordinal;
        LayoutKind = layoutKind;
        RowMode = rowMode;
        StorageMode = CellStorageMode.Normalized;
    }

    public int SheetDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public int Ordinal { get; private set; }
    public TableLayoutKind LayoutKind { get; private set; }
    public TableRowMode RowMode { get; private set; }
    public int? MaxDynamicRows { get; private set; }
    public int? HeaderStyleId { get; private set; }

    /// <summary>
    /// Фізична модель зберігання комірок саме цієї таблиці. Перехід на гібрид
    /// **вибірковий** — глобальний перехід не потрібен і надто дорогий (D-21).
    /// </summary>
    public CellStorageMode StorageMode { get; private set; }

    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    public IReadOnlyList<ColumnDef> Columns => _columns;
    public IReadOnlyList<RowDef> Rows => _rows;

    /// <summary>Чи може користувач додавати рядки.</summary>
    public bool AllowsDynamicRows => RowMode is TableRowMode.Dynamic or TableRowMode.Mixed;

    /// <summary>Перемикає модель зберігання за результатом гейта Етапу 0.</summary>
    public void SwitchStorage(CellStorageMode mode) => StorageMode = mode;
}
