using Ecr.Application.Common;
using Ecr.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ICollectionRunReader"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⚠ Сутність і з'єднання приєднуються в ТОМУ САМОМУ запиті: сторінка — один
/// похід у базу, а не один на рядок. Курсор — за <c>Id</c> униз, як у журналі
/// доставок: <c>Id</c> — <c>IDENTITY</c>, тож спадний порядок і є «новіші першими».
/// </remarks>
public sealed class CollectionRunReader(EcrDbContext db) : ICollectionRunReader
{
    /// <summary>Стеля інтервалів покриття в деталі одного прогону.</summary>
    internal const int MaxCoverage = 500;

    /// <inheritdoc />
    public async Task<PagedResult<CollectionRunView>> ListAsync(
        CollectionRunFilter filter, CursorRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(page);

        var decoded = Cursor.Decode(page.Cursor);
        var before = decoded == 0 ? long.MaxValue : decoded;

        // Локальні змінні — щоб умови стали параметрами запиту.
        var dataSourceId = filter.DataSourceId;
        var sourceEntityId = filter.SourceEntityId;
        var status = filter.Status;
        var from = filter.FromUtc;
        var to = filter.ToUtc;

        var rows = await Rows()
            .Where(r => r.Id < before)
            .Where(r => dataSourceId == null || r.DataSourceId == dataSourceId)
            .Where(r => sourceEntityId == null || r.SourceEntityId == sourceEntityId)
            .Where(r => status == null || r.Status == status)
            .Where(r => from == null || r.StartedAt >= from)
            .Where(r => to == null || r.StartedAt < to)
            .OrderByDescending(r => r.Id)
            .Take(page.Limit + 1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = rows.Take(page.Limit).Select(ToView).ToList();

        return new PagedResult<CollectionRunView>(
            items, rows.Count > page.Limit ? Cursor.Encode(items[^1].Id) : null, TotalCount: null);
    }

    /// <inheritdoc />
    public async Task<CollectionRunDetail?> FindAsync(long id, CancellationToken ct)
    {
        var row = await Rows().FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        var coverage = await db.CollectionCoverages
            .AsNoTracking()
            .Where(c => c.CollectionRunId == id)
            .OrderBy(c => c.CoveredFrom)
            .Take(MaxCoverage + 1)
            .Select(c => new CollectionRunCoverage(c.CoveredFrom, c.CoveredTo))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new CollectionRunDetail(
            ToView(row),
            row.ErrorMessage,
            [.. coverage.Take(MaxCoverage).Select(c => new CollectionRunCoverage(Utc(c.CoveredFrom), Utc(c.CoveredTo)))],
            coverage.Count > MaxCoverage);
    }

    private IQueryable<Row> Rows()
        => from r in db.CollectionRuns.AsNoTracking()
           join e in db.SourceEntities.AsNoTracking() on r.SourceEntityId equals e.Id
           join s in db.DataSources.AsNoTracking() on e.DataSourceId equals s.Id
           select new Row
           {
               Id = r.Id,
               SourceEntityId = r.SourceEntityId,
               SourceEntityCode = e.Code,
               SourceEntityName = e.DisplayName,
               DataSourceId = e.DataSourceId,
               DataSourceCode = s.Code,
               RangeFrom = r.RangeFrom,
               RangeTo = r.RangeTo,
               StartedAt = r.StartedAt,
               FinishedAt = r.FinishedAt,
               Status = r.Status,
               PointsRetrieved = r.PointsRetrieved,
               IsCatchUp = r.IsCatchUp,
               ErrorMessage = r.ErrorMessage,
               TriggeredByUserId = r.TriggeredByUserId,
           };

    private static CollectionRunView ToView(Row r)
        => new(
            r.Id, r.SourceEntityId, r.SourceEntityCode, r.SourceEntityName, r.DataSourceId, r.DataSourceCode,
            Utc(r.RangeFrom), Utc(r.RangeTo), Utc(r.StartedAt), r.FinishedAt is { } f ? Utc(f) : null,
            r.FinishedAt is { } end ? (long)(end - r.StartedAt).TotalMilliseconds : null,
            r.Status, r.PointsRetrieved, r.IsCatchUp, !string.IsNullOrEmpty(r.ErrorMessage), r.TriggeredByUserId);

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    /// <summary>Проєкція рядка журналу; клас, а не анонімний тип, — щоб фільтри йшли після <c>select</c>.</summary>
    private sealed class Row
    {
        public long Id { get; init; }
        public int SourceEntityId { get; init; }
        public string SourceEntityCode { get; init; } = null!;
        public string? SourceEntityName { get; init; }
        public int DataSourceId { get; init; }
        public string DataSourceCode { get; init; } = null!;
        public DateTime RangeFrom { get; init; }
        public DateTime RangeTo { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? FinishedAt { get; init; }
        public string Status { get; init; } = null!;
        public int PointsRetrieved { get; init; }
        public bool IsCatchUp { get; init; }
        public string? ErrorMessage { get; init; }
        public int? TriggeredByUserId { get; init; }
    }
}
