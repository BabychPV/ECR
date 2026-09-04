// src/Ecr.Domain/Entities/Dictionaries/RegistryValue.cs
using System.Globalization;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Значення одного поля запису довідника. Колонки **типізовані**, а не одне
/// текстове поле: інакше повернулася б та сама проблема, що з
/// <c>Attribute_XXXX</c> — тип відомий лише за домовленістю.
/// </summary>
/// <remarks>
/// Імена колонок — за схемою (`ValueNumeric`, `ValueRefEntryId`,
/// `ValueUnitId`), а не за скелетом: це `dic`-частина `Q-027`.
/// </remarks>
public sealed class RegistryValue : Entity<long>
{
    private RegistryValue() { }

    public RegistryValue(long registryEntryId, int registryFieldDefId)
    {
        RegistryEntryId = registryEntryId;
        RegistryFieldDefId = registryFieldDefId;
    }

    /// <summary>Значення для запису, який ще не збережений.</summary>
    /// <param name="entry">Запис-власник.</param>
    /// <param name="registryFieldDefId">Поле довідника.</param>
    /// <remarks>
    /// ⚠ Навігація, а не число: у нового запису <c>Id</c> дорівнює нулю до
    /// збереження, і <c>RegistryEntryId = entry.Id</c> зафіксувало б цей нуль
    /// назавжди. Через навігацію EF підставляє справжній ключ сам.
    /// </remarks>
    public RegistryValue(RegistryEntry entry, int registryFieldDefId)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
        RegistryEntryId = entry.Id;
        RegistryFieldDefId = registryFieldDefId;
    }

    public long RegistryEntryId { get; private set; }

    /// <summary>Запис-власник. Потрібна лише для вставки разом із новим записом.</summary>
    public RegistryEntry? Entry { get; private set; }
    public int RegistryFieldDefId { get; private set; }

    /// <summary>Числове значення. <c>decimal</c>, ніколи не <c>double</c> (D-30).</summary>
    public decimal? ValueNumeric { get; private set; }

    public string? ValueString { get; private set; }
    public DateTime? ValueDate { get; private set; }
    public bool? ValueBool { get; private set; }

    /// <summary>Посилання на інший запис довідника для полів типу <c>Lookup</c>.</summary>
    public long? ValueRefEntryId { get; private set; }

    /// <summary>Одиниця значення, якщо поле її має (ФВ-16.1).</summary>
    public int? ValueUnitId { get; private set; }

    /// <summary>Записує значення відповідно до типу поля.</summary>
    /// <param name="dataType">Тип поля з <c>RegistryFieldDef</c>.</param>
    /// <param name="value">Значення; <c>null</c> — очистити.</param>
    /// <param name="unitId">Одиниця; приймається лише для числових полів.</param>
    /// <exception cref="DomainException">
    /// Значення не приводиться до типу поля або одиниця вказана там, де її не
    /// може бути — <c>ECR-REG-0422</c>.
    /// </exception>
    /// <remarks>
    /// ⚠ Решта колонок **заноляється завжди**. Дві заповнені колонки означають,
    /// що тип поля колись змінили і старе значення лишилося: читач бере ту, яку
    /// очікує за типом, і мовчки отримує число, якого ніхто не вводив.
    /// </remarks>
    public void Set(CellDataType dataType, object? value, int? unitId)
    {
        Clear();

        if (unitId is not null && dataType is not (CellDataType.Decimal or CellDataType.Int))
        {
            throw new DomainException(
                "ECR-REG-0422",
                $"Одиницю вимірювання задано полю типу {dataType}: одиницю мають лише числові поля (ФВ-16.1).");
        }

        if (value is null)
        {
            return;
        }

        switch (dataType)
        {
            case CellDataType.String:
                ValueString = AsString(value);
                break;

            case CellDataType.Int:
            case CellDataType.Decimal:
                ValueNumeric = AsDecimal(value, dataType);
                ValueUnitId = unitId;
                break;

            case CellDataType.Bool:
                ValueBool = AsBool(value);
                break;

            case CellDataType.Date:
                ValueDate = AsDate(value);
                break;

            case CellDataType.Lookup:
                ValueRefEntryId = AsRef(value);
                break;

            case CellDataType.Unit:
                ValueUnitId = checked((int)AsDecimal(value, dataType));
                break;

            // ⛔ Formula і Calculated у довіднику не мають сенсу: обчислене
            // значення належить документу, а не довіднику, і збережене тут
            // застаріло б мовчки при першій зміні методології.
            case CellDataType.Formula:
            case CellDataType.Calculated:
            default:
                throw new DomainException(
                    "ECR-REG-0422", $"Поле довідника не може мати тип {dataType}.");
        }
    }

    /// <summary>Заноляє всі колонки значення.</summary>
    private void Clear()
    {
        ValueNumeric = null;
        ValueString = null;
        ValueDate = null;
        ValueBool = null;
        ValueRefEntryId = null;
        ValueUnitId = null;
    }

    private static string AsString(object value)
        => value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture)
           ?? throw new DomainException("ECR-REG-0422", "Значення не приводиться до рядка.");

    private static decimal AsDecimal(object value, CellDataType dataType)
    {
        // ⛔ double і float не приймаються навіть тут: вони вже втратили точність
        // до виклику, і перетворення її не поверне (D-30).
        if (value is double or float)
        {
            throw new DomainException(
                "ECR-REG-0422",
                $"Значення поля типу {dataType} передано як {value.GetType().Name}: "
                + "числа зберігаються лише як decimal (D-30).");
        }

        try
        {
            return System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new DomainException(
                "ECR-REG-0422", $"Значення «{value}» не є числом для поля типу {dataType}.");
        }
    }

    private static bool AsBool(object value)
    {
        try
        {
            return System.Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException)
        {
            throw new DomainException("ECR-REG-0422", $"Значення «{value}» не є логічним.");
        }
    }

    private static DateTime AsDate(object value)
        => value switch
        {
            DateTime dt => dt,
            DateOnly d => d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            string s when DateTime.TryParse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
            _ => throw new DomainException("ECR-REG-0422", $"Значення «{value}» не є датою."),
        };

    private static long AsRef(object value)
    {
        try
        {
            return System.Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new DomainException(
                "ECR-REG-0422", $"Значення «{value}» не є ідентифікатором запису довідника.");
        }
    }
}
