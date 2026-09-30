using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IRowWindowMapStore"/> над <see cref="EcrDbContext"/> (HSE301 A1).</summary>
public sealed class RowWindowMapStore(EcrDbContext db) : IRowWindowMapStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, ColumnDef>> FindColumnsAsync(
        IReadOnlyCollection<int> ids, CancellationToken ct)
        => await db.ColumnDefs
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id) && !c.IsDeleted)
            .ToDictionaryAsync(c => c.Id, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<RowWindowMap?> FindMapAsync(int id, CancellationToken ct)
        => db.RowWindowMaps.Include(m => m.Sources).FirstOrDefaultAsync(m => m.Id == id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RowWindowMap>> ListMapsAsync(int? tableDefId, int? sourceEntityId, CancellationToken ct)
        => await db.RowWindowMaps
            .AsNoTracking()
            .Include(m => m.Sources)
            .Where(m => tableDefId == null || m.TableDefId == tableDefId)
            .Where(m => sourceEntityId == null || m.Sources.Any(s => s.SourceEntityId == sourceEntityId))
            .OrderBy(m => m.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<bool> TargetTakenAsync(int tableDefId, int targetColumnDefId, CancellationToken ct)
        => db.RowWindowMaps.AnyAsync(m => m.TableDefId == tableDefId && m.TargetColumnDefId == targetColumnDefId, ct);

    /// <inheritdoc />
    public async Task<RowWindowMap> AddMapAsync(RowWindowMap map, CancellationToken ct)
    {
        db.RowWindowMaps.Add(map);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return map;
    }

    /// <inheritdoc />
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    /// <inheritdoc />
    public void ReleaseSources(RowWindowMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        db.RemoveRange(map.Sources);
    }

    /// <inheritdoc />
    public Task<int> CountValuesAsync(int mapId, CancellationToken ct)
        => db.RowWindowValues.CountAsync(v => v.RowWindowMapId == mapId, ct);

    /// <inheritdoc />
    public async Task RemoveMapAsync(RowWindowMap map, CancellationToken ct)
    {
        ReleaseSources(map);
        db.RowWindowMaps.Remove(map);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
