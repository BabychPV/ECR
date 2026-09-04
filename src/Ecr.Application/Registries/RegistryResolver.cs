// src/Ecr.Application/Registries/RegistryResolver.cs
using Ecr.Domain.Entities.Dictionaries;

namespace Ecr.Application.Registries;

/// <summary>
/// Темпоральний вибір записів довідника з урахуванням каскаду і фільтра.
/// Виділений окремо, бо потрібен і в UI, і у валідації, і в резолвінгу
/// посилань виразів — три місця з однаковим правилом.
/// </summary>
/// <remarks>
/// ⚠ Клас **не має залежностей** і нічого не читає: записи і зв'язки
/// передаються ззовні. Скелет описував його як <c>Resolve(registryDefId, asOf,
/// parentEntryId)</c> — без джерела даних, тобто нездійсненним; додані саме
/// два параметри з даними, решта лишилася (`D4-04`).
/// <para>
/// Чистота тут не стиль, а вимога: у валідації резолвінг викликається на
/// кожен рядок, і сховище всередині перетворило б перевірку документа на
/// тисячі запитів.
/// </para>
/// </remarks>
public sealed class RegistryResolver
{
    /// <summary>Чинні на дату записи з урахуванням батьківського вибору.</summary>
    /// <param name="entries">Усі записи довідника, без темпорального фільтра.</param>
    /// <param name="links">Вхідні зв'язки каскаду; порожньо — каскаду немає.</param>
    /// <param name="asOf">Дата **періоду**, а не «сьогодні».</param>
    /// <param name="parentEntryId">Обраний батьківський запис; <c>null</c> — без звуження.</param>
    /// <returns>Ідентифікатори у стабільному порядку: <c>Ordinal</c>, потім <c>Code</c>.</returns>
    public IReadOnlyList<long> Resolve(
        IReadOnlyList<RegistryEntry> entries,
        IReadOnlyList<RegistryEntryLink> links,
        DateOnly asOf,
        long? parentEntryId)
        => Select(entries, links, asOf, parentEntryId).Select(e => e.Id).ToList();

    /// <summary>Те саме, але сутностями: потрібно там, де крім <c>Id</c> треба назва.</summary>
    /// <param name="entries">Усі записи довідника.</param>
    /// <param name="links">Вхідні зв'язки каскаду.</param>
    /// <param name="asOf">Дата періоду.</param>
    /// <param name="parentEntryId">Обраний батьківський запис.</param>
    public IReadOnlyList<RegistryEntry> Select(
        IReadOnlyList<RegistryEntry> entries,
        IReadOnlyList<RegistryEntryLink> links,
        DateOnly asOf,
        long? parentEntryId)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(links);

        var allowed = BuildCascadeFilter(links, parentEntryId);

        return entries
            // 1. Чинність на дату періоду. Межі включні з обох боків: запис,
            //    закритий 30 червня, у звіті за 30 червня ще чинний.
            .Where(e => e.IsValidOn(asOf))

            // 2. Видалені й вимкнені не пропонуються. Але й не зникають: у
            //    комірках лежать їхні Id, і історія читається далі (ФВ-8.8).
            .Where(e => e.IsActive && !e.IsDeleted)

            // 3. Каскад (ФВ-8.4).
            .Where(e => allowed is null || allowed.Contains(e.Id) || e.ParentEntryId == parentEntryId)

            // 4. Стабільний порядок. Без нього список у UI «стрибав» би між
            //    запитами: база не зобов'язана повертати рядки однаково.
            .OrderBy(e => e.Ordinal)
            .ThenBy(e => e.Code, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Чи чинний конкретний запис на дату — та сама умова, що у списку.</summary>
    /// <param name="entry">Запис; <c>null</c> — вважається нечинним.</param>
    /// <param name="asOf">Дата періоду.</param>
    /// <remarks>
    /// ⚠ Саме через цей метод рахується <c>IsOrphaned</c>. Окрема, «майже
    /// така сама» умова в сканері розійшлася б зі списком на межах вікна — і
    /// рядок, значення якого користувач щойно обрав зі списку, ставав би
    /// осиротілим.
    /// </remarks>
    public bool IsSelectable(RegistryEntry? entry, DateOnly asOf)
        => entry is not null && entry.IsValidOn(asOf) && entry.IsActive && !entry.IsDeleted;

    /// <summary>
    /// Множина записів, дозволених обраним батьком; <c>null</c> — каскаду немає.
    /// </summary>
    private static HashSet<long>? BuildCascadeFilter(
        IReadOnlyList<RegistryEntryLink> links, long? parentEntryId)
    {
        if (parentEntryId is not { } parent)
        {
            return null;
        }

        // ⚠ Порожня множина — теж відповідь: «за цим дозволом водних об'єктів
        // немає». Повернути тут null означало б показати ВЕСЬ список, тобто
        // мовчки скасувати каскад саме там, де він потрібен найбільше.
        return links
            .Where(l => l.LeftEntryId == parent)
            .Select(l => l.RightEntryId)
            .ToHashSet();
    }
}
