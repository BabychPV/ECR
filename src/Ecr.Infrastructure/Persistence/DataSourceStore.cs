// src/Ecr.Infrastructure/Persistence/DataSourceStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDataSourceStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class DataSourceStore(EcrDbContext db) : IDataSourceStore
{
    /// <summary>
    /// Стеля переліку джерел.
    /// </summary>
    /// <remarks>
    /// ⚠ Джерел у системі одиниці — це підключення до PI AF і до FLERT, а не
    /// дані. Стеля стоїть не заради гортання, а щоб екран не намагався
    /// намалювати наслідок помилкового імпорту.
    /// </remarks>
    public const int MaxSources = 500;

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Лічильники рахуються ГРУПУВАННЯМ у базі, а не запитом на кожен рядок:
    /// джерел небагато, але запит у циклі однаково перетворив би перелік на
    /// N+1 рівно тоді, коли джерел стане більше.
    /// </remarks>
    public async Task<IReadOnlyList<DataSourceRow>> ListAsync(CancellationToken ct)
    {
        var sources = await db.DataSources.AsNoTracking()
            .OrderBy(s => s.Code)
            .Take(MaxSources)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // ⚠ Стеля стоїть і на лічильниках: груп не може бути більше, ніж джерел,
        // але межа має бути записана в запиті, а не у здогадці про дані.
        var entities = await db.SourceEntities.AsNoTracking()
            .GroupBy(e => e.DataSourceId)
            .Select(g => new { DataSourceId = g.Key, Count = g.Count() })
            .OrderBy(g => g.DataSourceId)
            .Take(MaxSources)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var schedules = await (from s in db.CollectionSchedules.AsNoTracking()
                               join e in db.SourceEntities.AsNoTracking() on s.SourceEntityId equals e.Id
                               group s by e.DataSourceId into g
                               select new { DataSourceId = g.Key, Count = g.Count() })
            .OrderBy(g => g.DataSourceId)
            .Take(MaxSources)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return
        [
            .. sources.Select(source => new DataSourceRow(
                source,
                new DataSourceUsage(
                    entities.Find(e => e.DataSourceId == source.Id)?.Count ?? 0,
                    schedules.Find(s => s.DataSourceId == source.Id)?.Count ?? 0))),
        ];
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ БЕЗ <c>AsNoTracking</c> і БЕЗ фільтра <c>IsActive</c>: це джерело
    /// зараз правитимуть, і вимкнене джерело — саме те, яке вмикають назад.
    /// </remarks>
    public async Task<DataSource?> FindAsync(int dataSourceId, CancellationToken ct)
        => await db.DataSources.FirstOrDefaultAsync(s => s.Id == dataSourceId, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> IsCodeTakenAsync(string code, int? exceptId, CancellationToken ct)
        => await db.DataSources.AsNoTracking()
            .AnyAsync(s => s.Code == code && (exceptId == null || s.Id != exceptId), ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<DataSourceUsage> CountUsageAsync(int dataSourceId, CancellationToken ct)
    {
        var entities = await db.SourceEntities.AsNoTracking()
            .CountAsync(e => e.DataSourceId == dataSourceId, ct)
            .ConfigureAwait(false);

        var schedules = await (from s in db.CollectionSchedules.AsNoTracking()
                               join e in db.SourceEntities.AsNoTracking() on s.SourceEntityId equals e.Id
                               where e.DataSourceId == dataSourceId
                               select s.Id)
            .CountAsync(ct)
            .ConfigureAwait(false);

        return new DataSourceUsage(entities, schedules);
    }

    /// <inheritdoc />
    public void Add(DataSource source) => db.DataSources.Add(source);

    /// <inheritdoc />
    public void Remove(DataSource source) => db.DataSources.Remove(source);
}
