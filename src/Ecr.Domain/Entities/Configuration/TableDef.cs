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

    /// <summary>
    /// Стеля кількості динамічних рядків.
    /// </summary>
    /// <remarks>
    /// Не формальність: зріз на 500×60 має бюджет 1.5 с, і таблиця, яка
    /// непомітно виросла до тисяч рядків, вибиває його для всіх.
    /// </remarks>
    /// <exception cref="DomainException">Таблиця не динамічна або межа не додатна.</exception>
    public void SetMaxDynamicRows(int? max)
    {
        if (max is { } m && m <= 0)
        {
            throw new DomainException("ECR-TMPL-0422", $"MaxDynamicRows має бути додатним; отримано {m}.");
        }

        if (max is not null && RowMode != TableRowMode.Dynamic)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"MaxDynamicRows має сенс лише для RowMode = Dynamic; у таблиці {Code} режим {RowMode}.");
        }

        MaxDynamicRows = max;
    }

    /// <summary>Додає колонку.</summary>
    /// <exception cref="DomainException">Колонка з таким кодом уже є.</exception>
    public void AddColumn(ColumnDef column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (_columns.Any(c => string.Equals(c.Code, column.Code, StringComparison.Ordinal)))
        {
            throw new DomainException(
                "ECR-TMPL-0409", $"Колонка з кодом {column.Code} у таблиці {Code} уже існує.");
        }

        _columns.Add(column);
    }

    /// <summary>Додає рядок фіксованої таблиці.</summary>
    /// <exception cref="DomainException">Рядок із таким ключем уже є, або таблиця динамічна.</exception>
    public void AddRow(RowDef row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (RowMode == TableRowMode.Dynamic)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Таблиця {Code} динамічна: її рядки створюються під час роботи, а не в шаблоні.");
        }

        if (_rows.Any(r => string.Equals(r.RowKeyValue, row.RowKeyValue, StringComparison.Ordinal)))
        {
            throw new DomainException(
                "ECR-TMPL-0409", $"Рядок із ключем {row.RowKeyValue} у таблиці {Code} уже існує.");
        }

        _rows.Add(row);
    }
}
