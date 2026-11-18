using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Дати, на які набуває чинності будь-яка версія методології-бібліотеки всередині життя версії
/// (<c>EffectiveFrom &lt; дата &lt; наступна версія</c>): саме на цих межах може змінитися версія бібліотеки, яку
/// <see cref="MethodologyStore.ResolveImportsAsync"/> дає версії-імпортерові для періодів.
/// </summary>
internal static class LibraryEditionDates
{
    /// <summary>Дати чинності опублікованих/виведених з обігу версій бібліотек у (початок; наступна версія).</summary>
    /// <param name="db">Контекст бази.</param>
    /// <param name="lifeStart">Дата чинності версії.</param>
    /// <param name="next">Дата чинності наступної версії тієї ж методології (виключно); <c>null</c> — без межі.</param>
    /// <param name="ct">Токен скасування.</param>
    public static Task<List<DateOnly>> LoadAsync(EcrDbContext db, DateOnly lifeStart, DateOnly? next, CancellationToken ct)
        => db.MethodologyVersions.AsNoTracking()
            .Join(
                db.Methodologies.AsNoTracking().Where(m => m.Kind == MethodologyKind.Library),
                v => v.MethodologyId, m => m.Id, (v, m) => v)
            .Where(v => v.EffectiveFrom != null && v.EffectiveFrom > lifeStart
                        && (next == null || v.EffectiveFrom < next)
                        && (v.Status == TemplateVersionStatus.Published || v.Status == TemplateVersionStatus.Deprecated))
            .Select(v => v.EffectiveFrom!.Value)
            .Distinct()
            .OrderBy(d => d)
            .ToListAsync(ct);
}
