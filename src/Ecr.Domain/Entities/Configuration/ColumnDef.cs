using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Колонка таблиці: тип, формат, довідник, одиниця.</summary>
public sealed class ColumnDef : Entity<int>
{
    private ColumnDef() { }

    public ColumnDef(int tableDefId, EcrCode code, LocalizedText header, int ordinal, CellDataType dataType)
    {
        TableDefId = tableDefId;
        Code = code.Value;
        HeaderL10n = header;
        Ordinal = ordinal;
        DataType = dataType;
    }

    public int TableDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText HeaderL10n { get; private set; } = null!;
    public int Ordinal { get; private set; }
    public CellDataType DataType { get; private set; }
    public byte? Precision { get; private set; }
    public byte? Scale { get; private set; }
    public bool IsReadOnly { get; private set; }
    public bool IsRequired { get; private set; }
    public bool IsHidden { get; private set; }
    public bool IsMonthColumn { get; private set; }
    public byte? MonthNumber { get; private set; }
    public string? DefaultValue { get; private set; }
    public string? DisplayFormat { get; private set; }

    /// <summary>Довідник для листбокса. Обов'язковий при <see cref="CellDataType.Lookup"/>.</summary>
    public int? LookupRegistryDefId { get; private set; }

    /// <summary>Звуження списку довідника.</summary>
    public string? LookupFilter { get; private set; }

    /// <summary>Каскад: список залежить від значення іншої колонки.</summary>
    public int? CascadeFromColumnId { get; private set; }

    /// <summary>Одиниця, в якій зберігаються значення колонки (ФВ-16.1).</summary>
    public int? UnitId { get; private set; }

    public bool IsBusinessKey { get; private set; }
    public bool IsScopeField { get; private set; }
    public bool IsIndexed { get; private set; }
    public int? StyleId { get; private set; }
    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    /// <summary>Чи значення обчислюється системою, а не вводиться користувачем.</summary>
    public bool IsComputed => DataType is CellDataType.Formula or CellDataType.Calculated;

    /// <summary>Перевіряє, чи значення сумісне з типом і обмеженнями колонки.</summary>
    /// <returns>Код помилки з каталогу або <c>null</c>, якщо все гаразд.</returns>
    public string? ValidateValue(CellValueData value)
        => throw new NotImplementedException(
            "TODO: перевірити IsWellFormed(); відповідність заповненого поля DataType; " +
            "IsRequired проти IsEmpty; для Lookup — наявність ValueRegistryEntryId; " +
            "для Unit — наявність ValueUnitId (R-A4); для Decimal — Precision/Scale. " +
            "Повернути ECR-CELL-0422 або ECR-CELL-4221 для IsComputed, або null.");
}
