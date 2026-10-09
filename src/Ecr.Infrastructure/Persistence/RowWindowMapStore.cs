using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
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
    public async Task<IReadOnlyList<RowWindowFetchRequest>> OpenInstancesAsync(
        int tableDefId, int limit, CancellationToken ct)
    {
        // Стан — того періоду проєкту документа, якому належить екземпляр: Scheduled ще нема що рахувати,
        // закритий не змінюється (так само відсіює й сама задача підтягування).
        var instances = await (
                from t in db.TableInstances.AsNoTracking()
                join d in db.Documents.AsNoTracking() on t.DocumentId equals d.Id
                join p in db.Periods.AsNoTracking()
                    on new { d.ProjectId, t.PeriodKeyValue } equals new { p.ProjectId, p.PeriodKeyValue }
                where t.TableDefId == tableDefId && (p.State == PeriodState.Open || p.State == PeriodState.Grace)
                orderby t.Id
                select new { t.Id, t.PeriodKeyValue })
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. instances.Select(i => new RowWindowFetchRequest(i.Id, i.PeriodKeyValue))];
    }

    /// <inheritdoc />
    public async Task<int> SupersedeFoldedValuesAsync(
        int rowWindowMapId, IReadOnlyList<RowWindowFetchRequest> instances, int sourceEntityId, string sourceField, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceField);

        if (instances.Count == 0)
        {
            return 0;
        }

        var ids = instances.Select(i => i.TableInstanceId).Distinct().ToList();
        var periods = instances.Select(i => i.PeriodKey).Distinct().ToList();

        // Запитом, а не через трекер — як і зняття чинного в RowWindowFetchJob (L3-03); ключ партиції в умові.
        return await db.RowWindowValues
            .Where(v => v.IsCurrent
                        && v.RowWindowMapId == rowWindowMapId
                        && periods.Contains(v.PeriodKey)
                        && ids.Contains(v.TableInstanceId)
                        && v.SourceEntityId == sourceEntityId
                        && v.SourceField == sourceField
                        && (v.Status == RowWindowValueStatus.Fetched || v.Status == RowWindowValueStatus.Partial))
            .ExecuteUpdateAsync(set => set.SetProperty(v => v.IsCurrent, false), ct)
            .ConfigureAwait(false);
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
