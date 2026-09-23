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

    /// <summary>
    /// Змінює заголовок колонки.
    /// </summary>
    /// <remarks>
    /// ⛔ До появи <c>PUT …/tables/{tableId}/columns/{code}</c> (<c>W5.2</c>)
    /// сеттера не існувало: колонку створював лише офлайновий генератор
    /// тестових даних, і повторний запис тим самим кодом (<c>D2-147</c>) не
    /// мав як змінити щойно введений заголовок. Той самий випадок, що й
    /// <see cref="SheetDef.Rename"/> у <c>W5.0</c>.
    ///
    /// ⚠ <c>HeaderL10n</c> — поле презентаційного шару (<see
    /// cref="Services.ChangeClassifier.PresentationFields"/>), тож дозволене і
    /// в опублікованій версії — так само, як назва аркуша.
    /// </remarks>
    public void Rename(LocalizedText header)
    {
        ArgumentNullException.ThrowIfNull(header);
        HeaderL10n = header;
    }

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
                $"Scale ({scl}) не може перевищувати Precision ({prc}) у колонці {Code}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.scaleExceedsPrecision",
                    ["columnCode"] = Code,
                    ["precision"] = prc.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["scale"] = scl.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
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
                $"Довідник можна прив'язати лише до колонки типу Lookup; у {Code} тип {DataType}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.lookupRequiresLookupType",
                    ["columnCode"] = Code,
                    ["dataType"] = DataType.ToString(),
                });
        }

        LookupRegistryDefId = registryDefId;
        LookupFilter = filter;
    }

    /// <summary>Одиниця, в якій зберігаються значення колонки (ФВ-16.1).</summary>
    /// <param name="unitId">Одиниця з <c>uom.Unit</c>.</param>
    /// <exception cref="DomainException">
    /// Колонка типу <see cref="CellDataType.Unit"/> — <c>ECR-TMPL-0422</c>.
    /// </exception>
    /// <remarks>
    /// ⛔ Колонці з одиницею НА РЯДОК одиниця колонки не задається (ФВ-16.8,
    /// R-A4): це означало б, що та сама комірка має дві одиниці одночасно, а
    /// котра з них правильна, з'ясувалося б на звірці.
    /// </remarks>
    public void SetUnit(int unitId)
    {
        if (DataType == CellDataType.Unit)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Колонка {Code} має тип Unit: одиниця задається на рядок "
                + "(doc.CellValue.ValueUnitId), а не на колонку.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0422.unitColumnHasRowUnit",
                    ["columnCode"] = Code,
                });
        }

        UnitId = unitId;
    }

    /// <summary>Значення за замовчуванням і формат відображення.</summary>
    public void SetPresentation(string? defaultValue, string? displayFormat, int? styleId)
    {
        DefaultValue = defaultValue;
        DisplayFormat = displayFormat;
        StyleId = styleId;
    }

    /// <summary>
    /// Приховує колонку від показу, лишаючи в структурі.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>IsHidden</c> — поле презентаційного шару (<see
    /// cref="Services.ChangeClassifier.PresentationFields"/>) ще ДО цього
    /// сеттера: класифікатор уже чекав на нього. ⛔ Сеттера просто не було —
    /// колонку створював лише офлайновий генератор, і ховати щойно створене
    /// не було звідки: той самий випадок, що й <see cref="SheetDef.SetVisible"/>
    /// у <c>W5.0</c>.
    /// </remarks>
    public void SetHidden(bool hidden) => IsHidden = hidden;

    /// <summary>
    /// Логічне видалення: фізично запис лишається, бо на нього посилаються
    /// комірки документів навіть у чернетці (ФВ-7.6) — той самий підхід, що
    /// й <see cref="SheetDef.SoftDelete"/>.
    /// </summary>
    public void SoftDelete(int userId, DateTime utcNow)
    {
        IsDeleted = true;
        DeletedAt = utcNow;
        DeletedByUserId = userId;
    }

    /// <summary>
    /// Стеля довжини рядкового значення комірки — та сама, що й у стовпця
    /// <c>doc.CellValue.ValueString</c> (<c>nvarchar(1000)</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ <c>DAT-03</c>. Межа стояла у ТРЬОХ місцях і не була доменним
    /// правилом у жодному: <c>CellValueConfiguration</c>
    /// (<c>HasMaxLength(1000)</c>), стовпець у базі і
    /// <c>NormalizedCellStore.AddNullable</c> (<c>Parameters.Add(name, type,
    /// 1000)</c>). Останнє обрізало значення МОВЧКИ: <c>SqlParameter</c> із
    /// заданим <c>Size</c> ріже рядок ще до відправки, тож СУБД отримувала
    /// рівно 1000 припустимих символів і не мала на що скаржитись. Виміряно
    /// мутацією: виклик <c>ICellStore.ApplyAsync</c> напряму з 1001 символом
    /// проходив БЕЗ винятку, а в <c>doc.CellValue</c> лишався рядок на 1000
    /// символів.
    ///
    /// ⚠ Але <c>200 OK</c> користувач на шляху <c>PATCH</c> НЕ бачив, і це
    /// варто знати точно (виміряно тією самою мутацією): одразу після
    /// значень <c>PatchCellsHandler</c> пише аудит, а <c>AuditWriter</c>
    /// віддає <c>NewValue</c> БЕЗ <c>Size</c> (<c>AddWithValue</c>) — тож
    /// повне значення впиралося в <c>aud.CellChange.NewValue</c>
    /// (теж <c>nvarchar(1000)</c>) і валило весь батч помилкою 2628. Запит
    /// закінчувався <c>500 ECR-SYS-0500</c> з відкатом. Тобто дефект був не
    /// «тиха втрата на шляху користувача», а ГІРША пара: тихе обрізання в
    /// сховищі, прикрите збоєм, який звинувачує журнал аудиту замість
    /// завеликого вводу. Обидві половини закриває ця перевірка: далі межі
    /// не доходить ані значення, ані його запис в аудит.
    ///
    /// ⚠ Константа ПУБЛІЧНА навмисно: клієнт (<c>maxLength</c> у редакторі
    /// комірки) і сховище мають називати одне число, а не три схожі. Межу
    /// тримає домен, СУБД лишається другим рубежем (<c>Size = -1</c> у
    /// сховищі), а не єдиним.
    /// </remarks>
    public const int MaxStringLength = 1000;

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

        // 5. Довжина рядка. Перевірка йде РАНІШЕ за числові обмеження, бо
        //    вона про сам факт збереження: усе, що довше за стовпець, або
        //    втрачається мовчки, або валить запис у СУБД — і перше гірше.
        //    Обрізати «як є» не можна з тієї самої причини, що й округлювати
        //    в п. 6: втрачений хвіст тексту виявиться лише на звірці, коли
        //    виправити його вже нема з чого.
        if (value.ValueString is { } text && text.Length > MaxStringLength)
        {
            return "ECR-CELL-0422";
        }

        // 6. Цілочислова колонка не приймає дробову частину.
        if (DataType == CellDataType.Int && value.ValueNumeric is { } asInt
            && decimal.Truncate(asInt) != asInt)
        {
            return "ECR-CELL-0422";
        }

        // 7. Precision і Scale. Округлити мовчки не можна: втрачений знак —
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
            if (char.IsDigit(ch))
            {
                digits++;
            }
        }

        // Провідний нуль у «0.5» цифрою precision не є.
        if (text.StartsWith("0.", StringComparison.Ordinal))
        {
            digits--;
        }
        return digits;
    }
}
