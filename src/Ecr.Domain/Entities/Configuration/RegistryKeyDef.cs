// src/Ecr.Domain/Entities/Configuration/RegistryKeyDef.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>
/// Складений ключ довідника: упорядкований набір 1–8 полів, комбінація
/// значень яких унікальна серед живих записів (FEATURE-REGISTRY-TABLES §4.1,
/// рішення <c>D-151</c>).
/// </summary>
/// <remarks>
/// ⛔ Склад, <see cref="IgnoreCase"/> і <see cref="IsPrimary"/> після створення
/// НЕ змінюються — з тієї самої причини, що <c>IsKey</c> поля
/// (<see cref="RegistryFieldDef.Update"/>): будь-яка з цих змін мовчки
/// перебудувала б хеш <c>dic.RegistryEntryKey</c> кожного запису. Потрібен
/// інший ключ — заводять новий, старий вимикають (<see cref="SetActive"/>).
///
/// ⚠ Порушення передумов конструктора — <see cref="ArgumentException"/>, а не
/// <see cref="DomainException"/>: відмову людині з власним <c>messageKey</c>
/// (<c>keyFieldTypeNotAllowed</c>, <c>keyFieldNotRequired</c>) дає обробник
/// опису ДО побудови ключа (крок RT-11). Сюди такий ключ доходить лише через
/// помилку в коді, і тоді 500 чесніший за вигаданий текст.
/// </remarks>
public sealed class RegistryKeyDef : Entity<int>
{
    /// <summary>Найбільша кількість полів у ключі (§4.1).</summary>
    public const int MaxFields = 8;

    private readonly List<RegistryKeyField> _fields = [];

    private RegistryKeyDef() { }

    /// <summary>Створює ключ довідника.</summary>
    /// <param name="registryDefId">Довідник, якому належить ключ.</param>
    /// <param name="code">Код ключа в межах довідника (<c>PK</c>, <c>BY_LEGACY_ID</c>).</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="fields">
    /// Поля ключа в порядку частин; цей самий порядок — порядок аргументів
    /// <c>REGFIND</c>. Поля мають бути збережені й належати цьому довіднику.
    /// </param>
    /// <param name="isPrimary">Первинний ключ: ним шукає <c>REGFIND</c>; усі поля обов'язкові.</param>
    /// <param name="ignoreCase">Чи порівнювати текстові частини без урахування регістру.</param>
    /// <param name="createdByUserId">Автор.</param>
    /// <param name="createdAt">Момент створення в UTC.</param>
    /// <exception cref="ArgumentException">
    /// Полів 0 або понад <see cref="MaxFields"/>, поле повторюється, не
    /// збережене, належить іншому довіднику, має тип, що не може бути частиною
    /// ключа, або первинний ключ містить необов'язкове поле.
    /// </exception>
    public RegistryKeyDef(
        int registryDefId,
        EcrCode code,
        LocalizedText name,
        IReadOnlyList<RegistryFieldDef> fields,
        bool isPrimary,
        bool ignoreCase,
        int createdByUserId,
        DateTime createdAt)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentOutOfRangeException.ThrowIfZero(fields.Count, nameof(fields));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fields.Count, MaxFields, nameof(fields));

        var seen = new HashSet<int>();
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i] ?? throw new ArgumentException($"Поле ключа №{i + 1} не задане.", nameof(fields));

            // ⚠ Лише збережене поле: Id нового дорівнює нулю, і FK зафіксував
            // би цей нуль. Обробник опису зберігає поля до ключів.
            if (!field.IsPersisted)
            {
                throw new ArgumentException($"Поле «{field.Code}» ще не збережене.", nameof(fields));
            }

            if (field.RegistryDefId != registryDefId)
            {
                throw new ArgumentException(
                    $"Поле «{field.Code}» належить довіднику {field.RegistryDefId}, а не {registryDefId}.",
                    nameof(fields));
            }

            if (!seen.Add(field.Id))
            {
                throw new ArgumentException($"Поле «{field.Code}» повторюється в ключі.", nameof(fields));
            }

            if (!AllowsPartType(field.DataType))
            {
                throw new ArgumentException(
                    $"Поле «{field.Code}» типу {field.DataType} не може бути частиною ключа.", nameof(fields));
            }

            // `D-153`: REGFIND мусить мати повну адресу — частина первинного
            // ключа не буває порожньою.
            if (isPrimary && !field.IsRequired)
            {
                throw new ArgumentException(
                    $"Поле «{field.Code}» необов'язкове, а первинний ключ вимагає обов'язкових полів.",
                    nameof(fields));
            }

            _fields.Add(new RegistryKeyField((byte)(i + 1), field.Id));
        }

        RegistryDefId = registryDefId;
        Code = code.Value;
        NameL10n = name;
        IsPrimary = isPrimary;
        IgnoreCase = ignoreCase;
        IsActive = true;
        CreatedByUserId = createdByUserId;
        CreatedAt = createdAt;
    }

    /// <summary>Довідник, якому належить ключ.</summary>
    public int RegistryDefId { get; private set; }

    /// <summary>Код ключа; унікальний у межах довідника.</summary>
    public string Code { get; private set; } = null!;

    /// <summary>Назва мовами каталогу.</summary>
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Первинний ключ: не більше одного активного на довідник (<c>UX_RegistryKeyDef_Primary</c>).</summary>
    public bool IsPrimary { get; private set; }

    /// <summary>Текстові частини порівнюються без урахування регістру (§4.2).</summary>
    public bool IgnoreCase { get; private set; }

    /// <summary>Чи діє ключ. Вимкнений ключ не перевіряється і не бере участі в унікальності первинного.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Момент створення в UTC.</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Автор.</summary>
    public int CreatedByUserId { get; private set; }

    /// <summary>Поля ключа в порядку частин (<see cref="RegistryKeyField.Ordinal"/> від 1).</summary>
    public IReadOnlyList<RegistryKeyField> Fields => _fields;

    /// <summary>Чи може поле такого типу бути частиною ключа (§4.1).</summary>
    /// <param name="dataType">Тип поля довідника.</param>
    /// <returns><c>false</c> для обчислюваних типів: їх значення в записі не зберігається.</returns>
    public static bool AllowsPartType(CellDataType dataType)
        => dataType is CellDataType.String or CellDataType.Int or CellDataType.Decimal
            or CellDataType.Bool or CellDataType.Date or CellDataType.Lookup or CellDataType.Unit;

    /// <summary>Змінює назву. Хеш від неї не залежить.</summary>
    /// <param name="name">Нова назва мовами каталогу.</param>
    public void Rename(LocalizedText name)
    {
        ArgumentNullException.ThrowIfNull(name);
        NameL10n = name;
    }

    /// <summary>Вмикає або вимикає ключ.</summary>
    /// <param name="isActive">Нове значення.</param>
    /// <remarks>
    /// Вимкнення замість видалення: рядки <c>dic.RegistryEntryKey</c>
    /// посилаються на ключ зовнішнім ключем, а історія опису мусить пояснювати,
    /// чому колись діяла саме така унікальність.
    /// </remarks>
    public void SetActive(bool isActive) => IsActive = isActive;
}
