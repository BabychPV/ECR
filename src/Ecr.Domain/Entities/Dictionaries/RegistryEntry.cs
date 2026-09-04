// src/Ecr.Domain/Entities/Dictionaries/RegistryEntry.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Dictionaries;

/// <summary>
/// Запис довідника. **Темпоральний**: чинність задається вікном
/// <see cref="ValidFrom"/>…<see cref="ValidTo"/>, і саме вона визначає, чи
/// можна обрати запис у періоді (ФВ-8.5).
/// </summary>
/// <remarks>
/// Фізично не видаляється ніколи, поки на нього посилаються дані (ФВ-8.6,
/// <c>ECR-REG-0409</c>). Перейменування не змінює історію, бо в комірці лежить
/// <c>Id</c>, а не текст (ФВ-8.8).
/// </remarks>
public sealed class RegistryEntry : Entity<long>
{
    private RegistryEntry() { }

    /// <summary>Створює запис довідника.</summary>
    /// <param name="registryDefId">Довідник, до якого належить запис.</param>
    /// <param name="code">Стабільний код; перейменування його не змінює (ФВ-8.8).</param>
    /// <param name="display">Локалізована назва для показу.</param>
    /// <param name="createdByUserId">Автор. Нуль — лише для фікстур і тестів.</param>
    /// <param name="createdAt">Час створення в UTC; <c>null</c> — заповнить сховище.</param>
    /// <remarks>
    /// ⚠ Два останні параметри необов'язкові навмисно: у схемі вони
    /// <c>NOT NULL</c>, але контракт тесту створює запис трьома аргументами
    /// (`RegistryEntryTests`). Обов'язковими зробити не можна — зламався б
    /// тест, а тести тут первинні щодо скелета.
    /// </remarks>
    public RegistryEntry(
        int registryDefId,
        EcrCode code,
        LocalizedText display,
        int createdByUserId = 0,
        DateTime? createdAt = null)
    {
        RegistryDefId = registryDefId;
        Code = code.Value;
        DisplayL10n = display;
        IsActive = true;
        CreatedByUserId = createdByUserId;
        CreatedAt = createdAt ?? DateTime.UnixEpoch;
    }

    public int RegistryDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText DisplayL10n { get; private set; } = null!;

    /// <summary>Початок вікна чинності; <c>null</c> — «завжди від початку».</summary>
    public DateOnly? ValidFrom { get; private set; }

    /// <summary>Кінець вікна; <c>null</c> — «без обмеження».</summary>
    public DateOnly? ValidTo { get; private set; }

    /// <summary>
    /// Порядок у списку. Саме він, а не <see cref="Code"/>, визначає, як
    /// записи лягають у випадний список: алфавітний порядок кодів для людини
    /// нічого не означає (`ФВ-8.2`).
    /// </summary>
    public int Ordinal { get; private set; }

    public bool IsActive { get; private set; }
    public bool IsDeleted { get; private set; }
    public long? ParentEntryId { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public int CreatedByUserId { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    /// <summary>Чинний на дату. Межі **включні** з обох боків.</summary>
    public bool IsValidOn(DateOnly date)
        => (ValidFrom is null || date >= ValidFrom)
        && (ValidTo   is null || date <= ValidTo);

    /// <summary>
    /// Змінює вікно чинності. Викликає перерахунок <c>IsOrphaned</c> на рядках,
    /// що посилаються на цей запис (ФВ-8.13a) — але **не тут**: сутність не
    /// знає про документи. Цим займається <c>SetEntryValidityHandler</c>.
    /// </summary>
    /// <param name="from">Початок вікна; <c>null</c> — від початку.</param>
    /// <param name="to">Кінець вікна; <c>null</c> — без обмеження.</param>
    /// <exception cref="DomainException">Кінець раніший за початок — <c>ECR-REG-0422</c>.</exception>
    public void SetValidity(DateOnly? from, DateOnly? to)
    {
        // Порожнє вікно — не «нічого не чинне», а помилка вводу: запис, який
        // не чинний ніколи, неможливо ні обрати, ні пояснити.
        if (from is { } start && to is { } end && end < start)
        {
            throw new DomainException(
                "ECR-REG-0422",
                $"Кінець вікна чинності {end} раніший за початок {start}.");
        }

        ValidFrom = from;
        ValidTo = to;
    }

    /// <summary>Ставить порядок у списку.</summary>
    /// <param name="ordinal">Позиція; від'ємна не має сенсу.</param>
    public void SetOrdinal(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        Ordinal = ordinal;
    }

    /// <summary>Змінює назву. Код і <c>Id</c> лишаються — історія не рухається (ФВ-8.8).</summary>
    /// <param name="display">Нова локалізована назва.</param>
    public void Rename(LocalizedText display)
    {
        ArgumentNullException.ThrowIfNull(display);
        DisplayL10n = display;
    }

    /// <summary>Ставить батьківський запис для ієрархічних довідників.</summary>
    /// <param name="parentEntryId">Батько; <c>null</c> — корінь.</param>
    /// <exception cref="DomainException">Запис не може бути власним батьком.</exception>
    public void SetParent(long? parentEntryId)
    {
        if (parentEntryId is { } parent && IsPersisted && parent == Id)
        {
            throw new DomainException(
                "ECR-REG-0422", $"Запис {Id} не може бути власним батьком.");
        }

        ParentEntryId = parentEntryId;
    }

    /// <summary>Логічне видалення: фізичне заборонене при посиланнях (ФВ-8.6).</summary>
    /// <param name="userId">Хто видалив; <c>null</c> — невідомо (фікстури).</param>
    /// <param name="utcNow">Коли; <c>null</c> — не фіксувати.</param>
    /// <remarks>
    /// ⚠ Запис лишається в таблиці НАЗАВЖДИ: у комірках лежить його <c>Id</c>,
    /// і фізичне видалення перетворило б історію на набір чисел без підписів.
    /// Перевірку посилань робить use-case через <c>IRegistryStore</c> — сутність про
    /// дані не знає.
    /// </remarks>
    public void SoftDelete(int? userId = null, DateTime? utcNow = null)
    {
        IsDeleted = true;
        IsActive = false;
        DeletedByUserId = userId;
        DeletedAt = utcNow;
    }

    /// <summary>Повертає видалений запис у обіг.</summary>
    /// <remarks>
    /// Потрібно тому, що видалення тут логічне: помилкове «видалення» має
    /// відкочуватися, інакше єдиний спосіб виправити його — новий запис із
    /// новим <c>Id</c>, а старі комірки лишаться вказувати на старий.
    /// </remarks>
    public void Restore()
    {
        IsDeleted = false;
        IsActive = true;
        DeletedByUserId = null;
        DeletedAt = null;
    }
}
