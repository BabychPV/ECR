using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRowActivityFilterSource"/> над <see cref="EcrDbContext"/> (D-425).</summary>
/// <param name="db">Контекст бази.</param>
public sealed class RowActivityFilterSource(EcrDbContext db) : IRowActivityFilterSource
{
    /// <inheritdoc />
    public async Task<RowActivityFilterResolution?> ResolveForMethodologyAsync(
        int methodologyId, IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableDefIds);

        var raw = await db.Methodologies
            .AsNoTracking()
            .Where(m => m.Id == methodologyId)
            .Select(m => m.RowActivityFilter)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return await ResolveAsync(raw, tableDefIds, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RowActivityFilterResolution?> ResolveForVersionAsync(
        int methodologyVersionId, IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tableDefIds);

        var raw = await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => v.Id == methodologyVersionId)
            .Join(db.Methodologies, v => v.MethodologyId, m => m.Id, (v, m) => m.RowActivityFilter)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return await ResolveAsync(raw, tableDefIds, ct).ConfigureAwait(false);
    }

    private async Task<RowActivityFilterResolution?> ResolveAsync(
        string? raw, IReadOnlyCollection<int> tableDefIds, CancellationToken ct)
    {
        // NULL = фільтра немає: один запит, і нічого більше (більшість методологій).
        if (raw is null)
        {
            return null;
        }

        var empty = new Dictionary<int, IReadOnlyList<int>>();

        // Зіпсований формат у базі не валить прогін: фільтр просто не діє (ендпоінт такого не запише).
        if (RowActivityFilterSpec.TryParse(raw) is not { } spec)
        {
            return new RowActivityFilterResolution(null, empty);
        }

        var registryId = await db.RegistryDefs
            .AsNoTracking()
            .Where(r => r.Code == spec.RegistryCode)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (registryId is null)
        {
            return new RowActivityFilterResolution(null, empty);
        }

        var entryId = await db.RegistryEntries
            .AsNoTracking()
            .Where(e => e.RegistryDefId == registryId && e.Code == spec.EntryCode && !e.IsDeleted)
            .Select(e => (long?)e.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (entryId is null)
        {
            return new RowActivityFilterResolution(null, empty);
        }

        var ids = tableDefIds.Distinct().ToList();
        var columns = ids.Count == 0
            ? []
            : await db.ColumnDefs
                .AsNoTracking()
                .Where(c => ids.Contains(c.TableDefId) && !c.IsDeleted && c.LookupRegistryDefId == registryId)
                .OrderBy(c => c.Id)
                .Select(c => new { c.TableDefId, c.Id })
                .ToListAsync(ct)
                .ConfigureAwait(false);

        return new RowActivityFilterResolution(
            entryId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            columns
                .GroupBy(c => c.TableDefId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)[.. g.Select(c => c.Id)]));
    }
}
