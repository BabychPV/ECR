using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Реалізація <see cref="IProjectVersionTimeline"/>: малий відсортований список рядків шкали на проєкт у спільному
/// <see cref="IMemoryCache"/> (короткий строк + явне скидання), чиста функція — <see cref="ProjectVersionTimeline.VersionOf"/>.
/// </summary>
/// <remarks>
/// ⚠ Скидання (<see cref="Invalidate"/>) локальне для процесу; інший вузол побачить нову шкалу за <see cref="Lifetime"/>.
/// Тому писарі беруть <see cref="VersionOfFreshAsync"/>, а не кеш.
/// ✎ Єдине місце (разом із <c>doc.fn_VersionOf</c>), де читається <c>Project.TemplateVersionId</c> для відповіді «яка версія».
/// </remarks>
public sealed class ProjectVersionTimelineService(IMemoryCache cache, EcrDbContext db) : IProjectVersionTimeline
{
    /// <summary>Строк життя запису кешу.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private sealed record Snapshot(IReadOnlyList<TimelineEntry> Entries, int? Current);

    private static string Key(int projectId) => $"ptl:{projectId}";

    /// <inheritdoc />
    public async Task<int> VersionOfAsync(int projectId, int periodKey, CancellationToken ct)
    {
        var snapshot = await LoadAsync(projectId, useCache: true, ct).ConfigureAwait(false);
        return ProjectVersionTimeline.VersionOf(snapshot.Entries, periodKey, snapshot.Current ?? 0);
    }

    /// <inheritdoc />
    public async Task<int> VersionOfFreshAsync(int projectId, int periodKey, CancellationToken ct)
    {
        var snapshot = await LoadAsync(projectId, useCache: false, ct).ConfigureAwait(false);
        return ProjectVersionTimeline.VersionOf(snapshot.Entries, periodKey, snapshot.Current ?? 0);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimelineEntry>> GetAsync(int projectId, CancellationToken ct)
        => (await LoadAsync(projectId, useCache: true, ct).ConfigureAwait(false)).Entries;

    /// <inheritdoc />
    public async Task<int?> CurrentVersionAsync(int projectId, CancellationToken ct)
        => (await LoadAsync(projectId, useCache: false, ct).ConfigureAwait(false)).Current;

    /// <inheritdoc />
    public void Invalidate(int projectId) => cache.Remove(Key(projectId));

    private async Task<Snapshot> LoadAsync(int projectId, bool useCache, CancellationToken ct)
    {
        if (useCache && cache.TryGetValue(Key(projectId), out Snapshot? hit) && hit is not null)
        {
            return hit;
        }

        var entries = await db.ProjectTemplateVersions
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.FromPeriodKey)
            .Select(t => new TimelineEntry(t.FromPeriodKey, t.TemplateVersionId))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var current = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => (int?)p.TemplateVersionId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var snapshot = new Snapshot(entries, current);
        cache.Set(Key(projectId), snapshot, Lifetime);
        return snapshot;
    }
}
