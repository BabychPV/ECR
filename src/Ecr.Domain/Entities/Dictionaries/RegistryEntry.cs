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

    public RegistryEntry(int registryDefId, EcrCode code, LocalizedText display)
    {
        RegistryDefId = registryDefId;
        Code = code.Value;
        DisplayL10n = display;
        IsActive = true;
    }

    public int RegistryDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText DisplayL10n { get; private set; } = null!;

    /// <summary>Початок вікна чинності; <c>null</c> — «завжди від початку».</summary>
    public DateOnly? ValidFrom { get; private set; }

    /// <summary>Кінець вікна; <c>null</c> — «без обмеження».</summary>
    public DateOnly? ValidTo { get; private set; }

    public bool IsActive { get; private set; }
    public bool IsDeleted { get; private set; }
    public long? ParentEntryId { get; private set; }

    /// <summary>Чинний на дату. Межі **включні** з обох боків.</summary>
    public bool IsValidOn(DateOnly date)
        => (ValidFrom is null || date >= ValidFrom)
        && (ValidTo   is null || date <= ValidTo);

    /// <summary>
    /// Змінює вікно чинності. Викликає перерахунок <c>IsOrphaned</c> на рядках,
    /// що посилаються на цей запис (ФВ-8.13a) — але **не тут**: сутність не
    /// знає про документи. Цим займається <c>SetEntryValidityHandler</c>.
    /// </summary>
    public void SetValidity(DateOnly? from, DateOnly? to)
        => throw new NotImplementedException(
            "TODO: перевірити from <= to, записати межі, підняти ModifiedAt. " +
            "Звуження вікна може осиротити рядки, розширення — повернути їх; " +
            "обидва напрямки обробляє IOrphanScanner.RescanForEntryAsync (D-98).");

    /// <summary>Логічне видалення: фізичне заборонене при посиланнях (ФВ-8.6).</summary>
    public void SoftDelete()
        => throw new NotImplementedException(
            "TODO: IsDeleted = true, IsActive = false. Перевірку посилань робить " +
            "use-case через ICellStore — сутність про дані не знає.");
}
