using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Integration;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICollectionStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// Джерело істини щодо того, за які інтервали дані вже є, — це
/// <c>itg.CollectionCoverage</c>, а не <c>Watermark</c> у розкладі:
/// watermark — оптимізація, а не стан, і його втрата не має коштувати даних
/// (ER-I-03).
/// </remarks>
public sealed class CollectionStore(EcrDbContext db, IClock clock) : ICollectionStore
{
    /// <summary>Стеля вибірки інтервалів покриття.</summary>
    private const int MaxIntervals = 100_000;

    /// <inheritdoc />
    public Task<SourceEntity?> FindSourceEntityAsync(int sourceEntityId, CancellationToken ct)
        => db.SourceEntities.FirstOrDefaultAsync(e => e.Id == sourceEntityId && e.IsActive, ct);

    /// <inheritdoc />
    public Task<DataSource?> FindDataSourceAsync(int dataSourceId, CancellationToken ct)
        => db.DataSources.FirstOrDefaultAsync(s => s.Id == dataSourceId && s.IsActive, ct);

    /// <inheritdoc />
    public async Task<long> StartRunAsync(
        int sourceEntityId,
        DateTime fromUtc,
        DateTime toUtc,
        bool isCatchUp,
        int? triggeredByUserId,
        CancellationToken ct)
    {
        var run = new CollectionRun(
            sourceEntityId, fromUtc, toUtc, isCatchUp, triggeredByUserId, clock.UtcNow);

        db.CollectionRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return run.Id;
    }

    /// <inheritdoc />
    public async Task FinishRunAsync(
        long collectionRunId,
        string status,
        int pointsRetrieved,
        string? errorMessage,
        CancellationToken ct)
    {
        var run = await db.CollectionRuns
            .FirstOrDefaultAsync(r => r.Id == collectionRunId, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Прогону збору {collectionRunId} не існує.");

        // ⚠ Відмова джерела — теж ЗАВЕРШЕННЯ, зі статусом і кодом. Прогін, що
        // лишився «Running» назавжди, виглядає як довгий: його чекають замість
        // того, щоб подивитися на джерело.
        run.Complete(status, pointsRetrieved, clock.UtcNow, errorMessage);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Upsert за ПРИРОДНИМ ключем, а не вставка. Повторний запуск того
    /// самого діапазону не має дублювати точок (ФВ-11.3) — саме це робить
    /// наздоганяння безпечним: інакше кожне відновлення після простою
    /// подвоювало б суму за період.
    /// </remarks>
    public async Task<int> UpsertRawPointsAsync(
        long collectionRunId,
        int sourceEntityId,
        IReadOnlyList<SourceDataPoint> points,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return 0;
        }

        var paths = points.Select(p => p.SourcePath).Distinct().ToList();
        var from = points.Min(p => p.Timestamp);
        var to = points.Max(p => p.Timestamp);

        // Одним запитом на батч, не по точці: батч — це тисячі точок, і
        // перевірка кожної окремо перетворила б збір на тисячі round-trip.
        var existing = await db.RawDataPoints
            .Where(p => p.SourceEntityId == sourceEntityId
                        && paths.Contains(p.SourcePath)
                        && p.Timestamp >= from
                        && p.Timestamp <= to)
            .Take(MaxIntervals)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var index = existing.ToDictionary(p => (p.SourcePath, p.Timestamp));
        var written = 0;

        foreach (var point in points)
        {
            var unitId = await ResolveUnitAsync(point.SourceUnitSymbol, ct).ConfigureAwait(false);

            if (index.TryGetValue((point.SourcePath, point.Timestamp), out var stored))
            {
                stored.SetValue(point.ValueNumeric, point.ValueString, unitId, point.Quality);
            }
            else
            {
                var entity = new RawDataPoint(
                    sourceEntityId, point.SourcePath, point.Timestamp, collectionRunId, clock.UtcNow);

                // ⛔ Значення лягає В ОДИНИЦІ ДЖЕРЕЛА (ФВ-16.10, D-79).
                // Конвертувати тут означало б, що повторний перерахунок з
                // архіву дасть інший результат, якщо мапінг одиниць за цей час
                // змінили — і ніхто не зможе сказати, яке число правильне.
                entity.SetValue(point.ValueNumeric, point.ValueString, unitId, point.Quality);
                db.RawDataPoints.Add(entity);
            }

            written++;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return written;
    }

    /// <inheritdoc />
    public async Task WriteCoverageAsync(
        long collectionRunId,
        int sourceEntityId,
        IReadOnlyList<TimeInterval> covered,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(covered);

        if (covered.Count == 0)
        {
            return;
        }

        foreach (var interval in covered)
        {
            db.CollectionCoverages.Add(
                new CollectionCoverage(sourceEntityId, interval.FromUtc, interval.ToUtc, collectionRunId));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimeInterval>> GetCoverageAsync(
        int sourceEntityId, DateTime notBefore, CancellationToken ct)
        => await db.CollectionCoverages
            .AsNoTracking()
            .Where(c => c.SourceEntityId == sourceEntityId && c.CoveredTo >= notBefore)
            .OrderBy(c => c.CoveredFrom)
            .Take(MaxIntervals)
            .Select(c => new TimeInterval(c.CoveredFrom, c.CoveredTo))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Стелі вибірки тут немає навмисно — вона вже є в самому запиті:
    /// мапінгів на одну сутність джерела одиниці, і <c>Take</c> міг би тихо
    /// відрізати саме той, чия одиниця змінилася.
    /// </remarks>
    public async Task<IReadOnlyList<EntityFieldMap>> GetFieldMapsAsync(
        int sourceEntityId, CancellationToken ct)
        => await db.EntityFieldMaps
            .AsNoTracking()
            .Where(m => m.SourceEntityId == sourceEntityId && m.IsActive)
            .OrderBy(m => m.SourceField)
            .Take(MaxFieldMaps)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>Стеля вибірки мапінгів; тисяча полів на одну сутність — вже аварія.</summary>
    private const int MaxFieldMaps = 1_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceEntityStatus>> ListSourceEntitiesAsync(CancellationToken ct)
    {
        // ⚠ Три запити на весь перелік, а не три на кожну сутність: джерел
        // десятки, і N+1 тут перетворив би екран конфігуратора на сотні
        // звернень до бази.
        var entities = await db.SourceEntities
            .AsNoTracking()
            .OrderBy(e => e.Code)
            .Take(MaxSourceEntities)
            .Select(e => new
            {
                e.Id,
                e.Code,
                e.DisplayName,
                e.EntityPath,
                e.IsActive,
                e.DataSourceId,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (entities.Count == 0)
        {
            return [];
        }

        var ids = entities.ConvertAll(e => e.Id);

        var transports = await db.DataSources
            .AsNoTracking()
            .Take(MaxSourceEntities)
            .Select(s => new { s.Id, s.Transport })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Останній прогін кожної сутності: беремо всі завершені за стелею і
        // згортаємо в пам'яті — вибірка на кожну сутність коштувала б N запитів.
        var runs = await db.CollectionRuns
            .AsNoTracking()
            .Where(r => ids.Contains(r.SourceEntityId))
            .OrderByDescending(r => r.StartedAt)
            .Take(MaxRunsScanned)
            .Select(r => new { r.SourceEntityId, r.FinishedAt, r.Status, r.PointsRetrieved, r.StartedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var coverage = await db.CollectionCoverages
            .AsNoTracking()
            .Where(c => ids.Contains(c.SourceEntityId))
            .OrderBy(c => c.CoveredFrom)
            .Take(MaxIntervals)
            .Select(c => new { c.SourceEntityId, c.CoveredFrom, c.CoveredTo })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var transportById = transports.ToDictionary(s => s.Id, s => s.Transport.ToString());

        var lastRun = runs
            .GroupBy(r => r.SourceEntityId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.StartedAt).First());

        var now = clock.UtcNow;

        return entities.ConvertAll(entity =>
        {
            var run = lastRun.GetValueOrDefault(entity.Id);

            var intervals = coverage
                .Where(c => c.SourceEntityId == entity.Id)
                .Select(c => new TimeInterval(c.CoveredFrom, c.CoveredTo))
                .ToList();

            // ⚠ Прогалина шукається ТИМ САМИМ кодом, що й наздоганяння:
            // друга реалізація «що таке дірка» показувала б на екрані одне,
            // а збирала б інше.
            var gaps = Ecr.Application.Integration.GapFinder.Find(intervals, now.AddDays(-GapLookbackDays), now);

            return new SourceEntityStatus(
                entity.Id,
                entity.Code,
                entity.DisplayName,
                entity.EntityPath,
                transportById.GetValueOrDefault(entity.DataSourceId, "—"),
                entity.IsActive,
                run is null ? null : new CollectionRunStatus(run.FinishedAt, run.Status, run.PointsRetrieved),
                gaps.Count == 0 ? null : gaps[0].From);
        });
    }

    /// <summary>Стеля переліку сутностей збору.</summary>
    private const int MaxSourceEntities = 5_000;

    /// <summary>Скільки прогонів проглядати, шукаючи останній по кожній сутності.</summary>
    private const int MaxRunsScanned = 20_000;

    /// <summary>Наскільки глибоко екран шукає прогалини.</summary>
    /// <remarks>
    /// Сорок п'ять діб — звітний місяць плюс пільговий строк, той самий обрій,
    /// що й у наздоганянні (<c>CollectionRunner.CatchUpLookback</c>). Різні
    /// обрії давали б екран, який показує «все добре», поки збирач наздоганяє.
    /// </remarks>
    private const int GapLookbackDays = 45;

    /// <summary>Одиниця джерела за її символом.</summary>
    /// <remarks>
    /// ⚠ Нерозпізнаний символ дає <c>null</c>, а не здогадку. Одиниця, взята
    /// навмання, — це число, помножене невідомо на що; порожня одиниця
    /// принаймні видима у звіті про збір (ФВ-16.12).
    /// </remarks>
    private async Task<int?> ResolveUnitAsync(string? symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        return await db.Units
            .AsNoTracking()
            .Where(u => u.Code == symbol)
            .Select(u => (int?)u.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
