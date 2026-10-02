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
                  select new ScheduledSourceEntity(s, e.Code, e.DisplayName, d.Id, d.Code, e.SourceKind))
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
                  select new ScheduledSourceEntity(s, e.Code, e.DisplayName, d.Id, d.Code, e.SourceKind))
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
                      e.Code, e.DisplayName, s == null ? null : s.Id, d.Id, d.Code, e.SourceKind))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CollectionSchedule>> FindDependentsAsync(int collectionScheduleId, CancellationToken ct)
        => await db.CollectionSchedules
            .Where(s => s.DependsOnScheduleId == collectionScheduleId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Скільки мс чекати замок залежностей (за замовчуванням 15 с).</summary>
    public int DependencyLockTimeoutMs { get; init; } = 15000;

    /// <inheritdoc />
    public async Task LockDependenciesAsync(int dataSourceId, CancellationToken ct)
    {
        // Транзакційний application-lock на з'єднання: два одночасні `PUT A→B` і `PUT B→A` не бачать один
        // одного під RCSI, тож без замка обидва пройшли б перевірку циклу (той самий прийом, що в UserStore).
        var result = new Microsoft.Data.SqlClient.SqlParameter("@rc", System.Data.SqlDbType.Int)
        {
            Direction = System.Data.ParameterDirection.Output,
        };
        await db.Database.ExecuteSqlRawAsync(
            "EXEC @rc = sp_getapplock @Resource = @res, @LockMode = N'Exclusive', "
            + "@LockOwner = N'Transaction', @LockTimeout = @timeout;",
            [
                result,
                new Microsoft.Data.SqlClient.SqlParameter("@res", $"ecr.collection-schedule-deps.{dataSourceId}"),
                new Microsoft.Data.SqlClient.SqlParameter("@timeout", DependencyLockTimeoutMs),
            ],
            ct).ConfigureAwait(false);

        if (result.Value is int code && code < 0)
        {
            // Таймаут (-1) і взаємоблокування (-2/-3) — конфлікт, а не збій: клієнт повторює запит.
            throw new Application.Errors.ConcurrencyConflictException(
                Ecr.Domain.Errors.ErrorCodes.JobStateConflict,
                $"Залежності розкладів з'єднання {dataSourceId} зараз змінює інший запит; повторіть.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-JOB-0409.collectionScheduleChanged" });
        }
    }

    /// <inheritdoc />
    public async Task<int?> ReadDependsOnAsync(int collectionScheduleId, CancellationToken ct)
        => await db.CollectionSchedules.AsNoTracking()
            .Where(s => s.Id == collectionScheduleId)
            .Select(s => s.DependsOnScheduleId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public bool IsForeignKeyViolation(Exception failure)
        // ⚠ Лише ключ залежності: інший 547 (напр. сутність джерела) — це не гонка залежностей, а справжня помилка.
        => failure is DbUpdateException { InnerException: Microsoft.Data.SqlClient.SqlException { Number: 547 } sql }
           && sql.Message.Contains("FK_CS_DependsOn", StringComparison.Ordinal);

    /// <inheritdoc />
    public void Add(CollectionSchedule schedule) => db.CollectionSchedules.Add(schedule);

    /// <inheritdoc />
    public void Remove(CollectionSchedule schedule) => db.CollectionSchedules.Remove(schedule);
}
