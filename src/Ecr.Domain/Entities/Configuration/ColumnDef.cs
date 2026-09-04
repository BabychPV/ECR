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

    /// <summary>Позиція колонки в таблиці.</summary>
    public void Reorder(int ordinal) => Ordinal = ordinal;

    /// <summary>Обов'язковість заповнення.</summary>
    public void SetRequired(bool required) => IsRequired = required;

    /// <summary>Заборона ручного вводу. Не те саме, що <see cref="IsComputed"/>:
    /// read-only колонку заповнює імпорт або збір, обчислювану — рушій.</summary>
    public void SetReadOnly(bool readOnly) => IsReadOnly = readOnly;

    /// <summary>
    /// Точність і масштаб для <see cref="CellDataType.Decimal"/>.
    /// </summary>
    /// <remarks>
    /// Масштаб не є форматуванням: значення з більшою кількістю знаків
    /// <b>відхиляється</b>, а не округлюється, — інакше втрачений знак тихо
    /// змінив би число у звіті.
    /// </remarks>
    public void SetNumericFormat(byte? precision, byte? scale)
    {
        if (precision is { } prc && scale is { } scl && scl > prc)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Scale ({scl}) не може перевищувати Precision ({prc}) у колонці {Code}.");
        }

        Precision = precision;
        Scale = scale;
    }

    /// <summary>Прив'язує колонку до довідника.</summary>
    /// <exception cref="DomainException">Тип колонки не <see cref="CellDataType.Lookup"/>.</exception>
    public void SetLookup(int registryDefId, string? filter = null)
    {
        if (DataType != CellDataType.Lookup)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Довідник можна прив'язати лише до колонки типу Lookup; у {Code} тип {DataType}.");
        }

        LookupRegistryDefId = registryDefId;
        LookupFilter = filter;
    }

    /// <summary>Одиниця, в якій зберігаються значення колонки (ФВ-16.1).</summary>
    public void SetUnit(int unitId) => UnitId = unitId;

    /// <summary>Значення за замовчуванням і формат відображення.</summary>
    public void SetPresentation(string? defaultValue, string? displayFormat, int? styleId)
    {
        DefaultValue = defaultValue;
        DisplayFormat = displayFormat;
        StyleId = styleId;
    }

    /// <summary>Перевіряє, чи значення сумісне з типом і обмеженнями колонки.</summary>
    /// <returns>Код помилки з каталогу або <c>null</c>, якщо все гаразд.</returns>
    public string? ValidateValue(CellValueData value)
    {
        ArgumentNullException.ThrowIfNull(value);

        // 1. Обчислювана колонка не приймає запис узагалі — навіть коректний
        //    за типом. Перевірка йде ПЕРШОЮ: інакше користувач отримав би
        //    скаргу на тип там, де запис заборонений у принципі.
        if (IsComputed)
        {
            return "ECR-CELL-4221";
        }

        // 2. Рівно одне заповнене поле (або жодного при IsEmpty) — R-B4.
        if (!value.IsWellFormed())
        {
            return "ECR-CELL-0422";
        }

        // 3. Обов'язковість. Явна порожнеча в обов'язковій колонці — це
        //    порушення: «я свідомо лишив порожнім» не скасовує вимогу.
        if (value.IsEmpty)
        {
            return IsRequired ? "ECR-CELL-0422" : null;
        }

        // 4. Заповнене поле має відповідати типу колонки.
        var matchesType = DataType switch
        {
            CellDataType.String => value.ValueString is not null,
            CellDataType.Int => value.ValueNumeric is not null,
            CellDataType.Decimal => value.ValueNumeric is not null,
            CellDataType.Bool => value.ValueBool is not null,
            CellDataType.Date => value.ValueDate is not null,
            // Lookup зберігає ІДЕНТИФІКАТОР запису, а не його підпис (ФВ-8.8):
            // текст у такій колонці означає, що клієнт надіслав Display
            // замість Id, і мовчки прийняти його не можна.
            CellDataType.Lookup => value.ValueRegistryEntryId is not null,
            // Unit зберігає посилання на одиницю (R-A4).
            CellDataType.Unit => value.ValueUnitId is not null,
            _ => false
        };

        if (!matchesType)
        {
            return "ECR-CELL-0422";
        }

        // 5. Цілочислова колонка не приймає дробову частину.
        if (DataType == CellDataType.Int && value.ValueNumeric is { } asInt
            && decimal.Truncate(asInt) != asInt)
        {
            return "ECR-CELL-0422";
        }

        // 6. Precision і Scale. Округлити мовчки не можна: втрачений знак —
        //    це змінене число у звіті, і виявиться воно лише на звірці.
        if (DataType == CellDataType.Decimal && value.ValueNumeric is { } dec)
        {
            if (Scale is { } scale && decimal.Round(dec, scale, MidpointRounding.AwayFromZero) != dec)
            {
                return "ECR-CELL-0422";
            }

            if (Precision is { } precision)
            {
                var digits = CountSignificantDigits(dec);
                if (digits > precision)
                {
                    return "ECR-CELL-0422";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Кількість значущих цифр у <see cref="decimal"/> — те, що SQL Server
    /// називає <c>precision</c>.
    /// </summary>
    private static int CountSignificantDigits(decimal value)
    {
        var text = Math.Abs(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var digits = 0;
        foreach (var ch in text)
        {
            if (char.IsDigit(ch)) digits++;
        }

        // Провідний нуль у «0.5» цифрою precision не є.
        if (text.StartsWith("0.", StringComparison.Ordinal)) digits--;
        return digits;
    }
}
