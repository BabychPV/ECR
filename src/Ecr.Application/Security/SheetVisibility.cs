using Ecr.Domain.Enums;

namespace Ecr.Application.Security;

/// <summary>
/// Чи є в профілі ХОЧ ЯКІСЬ інструменти, що ховають аркуші, — для агрегатів, які не можуть рахувати по
/// кожному аркушу окремо (UI-33, R-8).
/// </summary>
/// <remarks>
/// ⛔ Агрегат «скільки аркушів не подано» за побудовою охоплює всі аркуші проєкту/кампанії. Якщо в читача
/// є явна заборона чи грант рівня <c>None</c> на аркуш, таблицю чи колонку (або заборона/звуження в ролі з
/// областю), аркуші, яких він не бачить, потрапили б у число — і різниця між двома відповідями (з забороною
/// й без) була б оракулом «там є приховане». Точний scope-aware підрахунок вимагає межі читання кожного
/// документа (шаблон версії, <c>DocumentReadScope</c>), що для агрегату по кампанії неприйнятно дорого, тому
/// тут свідомо консервативний бік: є бодай один такий інструмент — число НЕ віддається (<c>null</c>).
///
/// ⚠ Це не заміна <see cref="DocumentReadScope"/>: функція відповідає лише на «чи можна рахувати за ВСІМА
/// аркушами, нічого не розкривши». Кампанійне право <c>Report.ViewCampaign</c> за рішенням людини Q15-07
/// глобальне й без гранта на проєкт, тож рівень проєкту тут не вимагається; для календаря проєкту
/// (<c>projectId</c> задано) читач ще й має бачити проєкт повністю (грант без звуження).
/// </remarks>
public static class SheetVisibility
{
    /// <summary>Чи може читач без розкриття прихованого отримати суму по ВСІХ аркушах.</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="projectId">Проєкт календаря; <c>null</c> — огляд кампанії по всіх проєктах.</param>
    public static bool SeesAllSheets(AccessProfile profile, int? projectId = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Будь-яка явна заборона (на проєкт, аркуш, таблицю, колонку чи довідник): ключі несуть лише
        // ідентифікатор ресурсу, а не проєкт, тож «чужа» заборона розрізнити не можна — закрито за замовчуванням.
        if (profile.Denies.Count > 0 || HasLowGrant(profile.Grants))
        {
            return false;
        }

        // Звуження аркушами (<c>NarrowedAccess.SheetCodes</c>) саме по собі ховає аркуші поза списком, навіть без жодної
        // заборони: друга роль читача зі scope sheets=[A] відкриває A, а B для нього не існує (R-8).
        foreach (var scoped in profile.Scoped.Values)
        {
            if (scoped.Denies.Count > 0
                || HasLowGrant(scoped.Grants)
                || scoped.Narrowed.Any(l => l.SheetCodes is not null || l.Denies.Count > 0 || HasLowGrant(l.Grants)))
            {
                return false;
            }
        }

        // Календар одного проєкту: проєкт має бути відкритий читачеві без звуження ролі (D-214) —
        // роль, звужена аркушами, відкриває документ, але не всі аркуші.
        return projectId is not { } project
               || profile.LevelFor(ResourceKind.Project, project) >= GrantLevel.Read;
    }

    /// <summary>Грант рівня нижче <c>Read</c> на аркуш/таблицю/колонку — ховає ресурс так само, як заборона.</summary>
    private static bool HasLowGrant(IReadOnlyDictionary<string, GrantLevel> grants)
    {
        foreach (var (key, level) in grants)
        {
            if (level < GrantLevel.Read && !key.StartsWith($"{ResourceKind.Project}:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
