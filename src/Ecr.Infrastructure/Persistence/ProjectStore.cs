using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IProjectStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class ProjectStore(EcrDbContext db) : IProjectStore
{
    /// <inheritdoc />
    public async Task<PagedResult<ProjectSummary>> ListAsync(CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);

        var after = Cursor.Decode(page.Cursor);

        // Періоди не завантажуються — рахується лише їхня кількість. Повний
        // граф на сотні проєктів дав би сотні тисяч рядків заради двох колонок
        // на екрані.
        var rows = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id > after)
            .OrderBy(p => p.Id)
            .Take(page.Limit + 1)
            .Select(p => new ProjectSummary(
                p.Id,
                p.Code,
                p.Status,
                p.TimeZoneId,
                p.PeriodKind,
                p.CurrentPeriodId,
                db.Periods.Count(period => period.ProjectId == p.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > page.Limit;
        var items = rows.Take(page.Limit).ToList();

        return new PagedResult<ProjectSummary>(
            items, hasMore ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }
}
