// src/Ecr.Application/Permits/Groups/LookupTargetMatcher.cs
using Ecr.Application.Security;

namespace Ecr.Application.Permits.Groups;

/// <summary>
/// ЄДИНА точка порівняння «реєстр запису ↔ ціль Lookup колонки/поля» (Permits, PRMG): звичайна ціль (<c>LAND_*</c>,
/// <c>PRM_&lt;TYPE&gt;</c>) - збіг <c>RegistryDefId</c>, ціль-група - членство зі знімка
/// <see cref="ILookupTargetResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Споживачі НЕ порівнюють <c>RegistryDefId</c> цілі напряму (сторож <c>LookupTargetComparisonGuardTests</c> у
/// <c>Ecr.Architecture.Tests</c>): інакше пропущене місце тихо відхиляє записи групи або, гірше, приймає зайве.
/// </para>
/// <para>
/// Швидкий шлях: збіг реєстру - <c>true</c> без звернення до резолвера (Land: 0 нових запитів і 0 викликів).
/// Лише при незбігу кличеться <see cref="IPermitGroupResolver.Accepts"/> (або <c>AcceptsFor</c> із профілем -
/// S18: реєстр запису під Deny нерозрізнювано від «запису немає»).
/// </para>
/// </remarks>
public static class LookupTargetMatcher
{
    /// <summary>Чи належить запис реєстру <paramref name="entryRegistryId"/> цілі <paramref name="targetRegistryId"/>.</summary>
    /// <param name="groups">Резолвер груп; <c>null</c> (прямий конструктор у тестах) - груп немає.</param>
    /// <param name="profile">Профіль користувача для S18; <c>null</c> - без перевірки Deny (системні шляхи).</param>
    /// <param name="targetRegistryId">Ціль колонки/поля; <c>null</c> - колонка без цілі, нічого не належить.</param>
    /// <param name="entryRegistryId"><c>RegistryDefId</c> запису.</param>
    public static bool Belongs(
        IPermitGroupResolver? groups,
        AccessProfile? profile,
        int? targetRegistryId,
        int entryRegistryId)
    {
        if (targetRegistryId is not { } target)
        {
            return false;
        }

        if (entryRegistryId == target)
        {
            return true;
        }

        if (groups is null)
        {
            return false;
        }

        return profile is null
            ? groups.Accepts(target, entryRegistryId)
            : groups.AcceptsFor(profile, target, entryRegistryId);
    }

    /// <summary>
    /// Оновлює знімок груп, лише коли серед пар є незбіг (гілка, недосяжна для Land). Для збігу - без звернення до резолвера.
    /// </summary>
    /// <param name="groups">Резолвер груп; <c>null</c> - нічого не робить.</param>
    /// <param name="pairs">Пари (ціль колонки/поля, реєстр запису).</param>
    /// <param name="ct">Токен скасування.</param>
    public static ValueTask EnsureFreshOnMismatchAsync(
        IPermitGroupResolver? groups,
        IEnumerable<(int? Target, int EntryRegistryId)> pairs,
        CancellationToken ct)
    {
        if (groups is null)
        {
            return ValueTask.CompletedTask;
        }

        foreach (var (target, entry) in pairs)
        {
            if (target is { } t && t != entry)
            {
                return groups.EnsureFreshAsync(ct);
            }
        }

        return ValueTask.CompletedTask;
    }
}
