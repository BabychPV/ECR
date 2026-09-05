using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IStyleCatalog"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⚠ Читає стилі версії <b>одним запитом</b>. Колонок у версії тисячі, стилів
/// — десятки; запит на стиль кожної колонки перетворив би експорт книги на
/// тисячі звернень і вивів би його за бюджет 10 с ще до того, як записано
/// перше значення.
/// </remarks>
public sealed class StyleCatalog(EcrDbContext db) : IStyleCatalog
{
    /// <summary>Стеля вибірки; сотня стилів на версію — уже нетипово.</summary>
    private const int MaxStyles = 1_000;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, StyleDef>> GetAsync(
        int templateVersionId, CancellationToken ct)
    {
        var styles = await db.StyleDefs
            .AsNoTracking()
            .Where(s => s.TemplateVersionId == templateVersionId)
            .OrderBy(s => s.Id)
            .Take(MaxStyles)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return styles.ToDictionary(s => s.Id);
    }
}
