// src/Ecr.Application/Permits/Groups/LookupTargetMatcher.cs
using Ecr.Application.Registries;
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
/// Швидкий шлях: збіг реєстру - без звернення до резолвера (Land: 0 нових запитів і 0 викликів); без профілю - завжди
/// <c>true</c>, із профілем - <c>true</c>, якщо на реєстр цілі немає Deny (перевірка у пам'яті, <c>profile.Denies</c>).
/// Лише при незбігу кличеться <see cref="IPermitGroupResolver.Accepts"/> (або <c>AcceptsFor</c> із профілем -
/// S18: реєстр запису під Deny нерозрізнювано від «запису немає»).
/// </para>
/// <para>
/// Deny блокує і ВИБІР запису (рішення людини 23.10): Deny на реєстр забороняє не лише читання, а й вибір запису цього
/// реєстру в Lookup шапки/комірки - як для прямої цілі, так і для члена групи. Відмова - та сама, що для неіснуючого
/// запису (<c>ECR-HDR-0422 lookupEntryMissing</c> / <c>ECR-CELL-4223 missingEntry</c>), без оракула існування.
/// Системні шляхи (<c>profile = null</c>) Deny не перевіряють.
/// </para>
/// </remarks>
public static class LookupTargetMatcher
{
    /// <summary>Чи належить запис реєстру <paramref name="entryRegistryId"/> цілі <paramref name="targetRegistryId"/>.</summary>
    /// <param name="groups">Резолвер груп; <c>null</c> (прямий конструктор у тестах) - груп немає.</param>
    /// <param name="profile">Профіль користувача для S18; <c>null</c> - без перевірки Deny (системні шляхи).</param>
    /// <param name="targetRegistryId">Ціль колонки/поля; <c>null</c> - колонка без цілі, нічого не належить.</param>
    /// <param name="entryRegistryId"><c>RegistryDefId</c> запису.</param>
    /// <param name="viewProjectId">
    /// Проєкт документа для scoped-ролей: <c>Registry.View</c> з області цього проєкту теж відкриває реєстр. Викликач
    /// визначає його ЛИШЕ коли профіль без глобального права й зі scoped-ролями (<see cref="ViewProjectNeeded"/>).
    /// </param>
    public static bool Belongs(
        IPermitGroupResolver? groups,
        AccessProfile? profile,
        int? targetRegistryId,
        int entryRegistryId,
        int? viewProjectId = null)
    {
        if (targetRegistryId is not { } target)
        {
            return false;
        }

        // Рішення людини 25.10 («Так, вимагати»): вибір запису реєстру потребує права його ЧИТАТИ - ТОЙ САМИЙ критерій,
        // що й пікер (Registry.View глобально АБО грант Read на реєстр запису), плюс Registry.View у проєкті документа
        // для scoped-ролей. Без нього - «не належить» = та сама відповідь, що й для неіснуючого запису (без оракула
        // існування id). Системні шляхи (profile = null) і збір (IsIntegrationWriter) - як було. Усе в пам'яті профілю.
        if (profile is not null && !profile.IsIntegrationWriter && !CanViewRegistry(profile, entryRegistryId, viewProjectId))
        {
            return false;
        }

        if (entryRegistryId == target)
        {
            // P2 (рішення людини 23.10: «Deny блокує і вибір запису»): Deny на ПРЯМУ ціль так само відсікає запис, як і
            // реєстр члена групи (AcceptsFor) - «не належить» = та сама відповідь, що й для неіснуючого запису.
            // Перевірка у пам'яті (profile.Denies): нових запитів у гарячому шляху Land немає.
            return profile is null || !RegistryAccess.IsDeniedIn(profile, target, viewProjectId);
        }

        if (groups is null)
        {
            return false;
        }

        return profile is null
            ? groups.Accepts(target, entryRegistryId)
            : groups.AcceptsFor(profile, target, entryRegistryId);
    }

    private const string ViewPermission = "Registry.View";

    /// <summary>
    /// Чи може профіль ОБИРАТИ записи реєстру <paramref name="registryDefId"/> у документі: <c>Registry.View</c> (глобально,
    /// scoped на проєкт документа) або грант Read на реєстр - і без Deny. <c>null</c> (системні шляхи) і збір - так.
    /// ЄДИНЕ правило для перегляду імпорту (ті самі відповіді, що й <see cref="Belongs"/>).
    /// </summary>
    /// <param name="profile">Профіль; <c>null</c> - системний шлях.</param>
    /// <param name="registryDefId">Реєстр запису.</param>
    /// <param name="viewProjectId">Проєкт документа для scoped-ролей (<see cref="ViewProjectNeeded"/>).</param>
    public static bool MayChoose(AccessProfile? profile, int registryDefId, int? viewProjectId)
        => profile is null
           || profile.IsIntegrationWriter
           || CanViewRegistry(profile, registryDefId, viewProjectId);

    private static bool CanViewRegistry(AccessProfile profile, int registryDefId, int? viewProjectId)
    {
        // P50: Deny (спільний або виданий ролі з областю цього проєкту) перебиває будь-який View/грант.
        if (RegistryAccess.IsDeniedIn(profile, registryDefId, viewProjectId))
        {
            return false;
        }

        if (PermissionCheck.IsGranted(profile, ViewPermission)
            || profile.LevelFor(Domain.Enums.ResourceKind.Registry, registryDefId) >= Domain.Enums.GrantLevel.Read)
        {
            return true;
        }

        return viewProjectId is { } project
               && profile.Scoped.TryGetValue(project, out var scoped)
               && (scoped.RegistryView
                   || (scoped.RegistryGrants.TryGetValue(registryDefId, out var level) && level >= Domain.Enums.GrantLevel.Read));
    }

    /// <summary>
    /// Чи потрібно викликачеві визначати проєкт документа для перевірки <c>Registry.View</c>: лише профіль БЕЗ
    /// глобального права, але зі scoped-ролями (решта не платить запитом).
    /// </summary>
    /// <param name="profile">Профіль; <c>null</c> (системні шляхи) і збір - ні.</param>
    public static bool ViewProjectNeeded(AccessProfile? profile)
        => profile is { IsIntegrationWriter: false, Scoped.Count: > 0 }
           && (!PermissionCheck.IsGranted(profile, ViewPermission)
               || profile.Scoped.Values.Any(s => s.RegistryDenies.Count > 0));

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
