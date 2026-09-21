// src/Ecr.Infrastructure/Persistence/CollectionScheduleStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.External;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICollectionScheduleStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class CollectionScheduleStore(EcrDbContext db) : ICollectionScheduleStore
{
    /// <summary>
    /// Стеля переліку — та сама, що й у старті (<c>MaxCollectionSchedules</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Число повторене, а не позичене: <c>Ecr.Api</c> посилається на
    /// <c>Ecr.Infrastructure</c>, а не навпаки. Розбіжність не мовчазна —
    /// екран, який показує більше, ніж старт ставить, одразу видно за
    /// <c>lastError</c> у рядках понад стелю.
    /// </remarks>
    public const int MaxSchedules = 1_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScheduledSourceEntity>> ListAsync(string? dataSourceCode, CancellationToken ct)
        => await (from s in db.CollectionSchedules.AsNoTracking()
                  join e in db.SourceEntities.AsNoTracking() on s.SourceEntityId equals e.Id
                  join d in db.DataSources.AsNoTracking() on e.DataSourceId equals d.Id
                  where dataSourceCode == null || d.Code == dataSourceCode
                  orderby e.Code
                  select new ScheduledSourceEntity(s, e.Code, e.DisplayName, d.Id, d.Code))
            .Take(MaxSchedules)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ БЕЗ <c>AsNoTracking</c>: цей розклад зараз правитимуть. І на приєднаних
    /// наборах теж — він вимикає відстеження для ВСЬОГО запиту, і правка мовчки
    /// не доходить до бази (перевірено тестом версії рядка).
    /// </remarks>
    public async Task<ScheduledSourceEntity?> FindAsync(int collectionScheduleId, CancellationToken ct)
        => await (from s in db.CollectionSchedules
                  join e in db.SourceEntities on s.SourceEntityId equals e.Id
                  join d in db.DataSources on e.DataSourceId equals d.Id
                  where s.Id == collectionScheduleId
                  select new ScheduledSourceEntity(s, e.Code, e.DisplayName, d.Id, d.Code))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Лівим приєднанням, а не двома запитами: сутність без розкладу — це
    /// звичайний випадок створення, а не «не знайдено».
    /// </remarks>
    public async Task<SourceEntityScheduling?> FindSourceEntityAsync(int sourceEntityId, CancellationToken ct)
        => await (from e in db.SourceEntities.AsNoTracking()
                  where e.Id == sourceEntityId
                  join d in db.DataSources.AsNoTracking() on e.DataSourceId equals d.Id
                  join s in db.CollectionSchedules.AsNoTracking() on e.Id equals s.SourceEntityId into schedules
                  from s in schedules.DefaultIfEmpty()
                  select new SourceEntityScheduling(
                      e.Code, e.DisplayName, s == null ? null : s.Id, d.Id, d.Code))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void Add(CollectionSchedule schedule) => db.CollectionSchedules.Add(schedule);

    /// <inheritdoc />
    public void Remove(CollectionSchedule schedule) => db.CollectionSchedules.Remove(schedule);
}
