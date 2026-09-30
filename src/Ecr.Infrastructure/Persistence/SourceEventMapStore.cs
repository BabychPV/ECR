using System.Globalization;
using System.Text;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="ISourceEventMapStore"/> над <see cref="EcrDbContext"/> (HSE301 A6).</summary>
public sealed class SourceEventMapStore(EcrDbContext db) : ISourceEventMapStore
{
    /// <inheritdoc />
    public async Task<EventMapTableInfo?> FindTargetTableAsync(int tableDefId, CancellationToken ct)
    {
        var row = await (from t in db.TableDefs.AsNoTracking()
                         join s in db.SheetDefs.AsNoTracking() on t.SheetDefId equals s.Id
                         where t.Id == tableDefId && !t.IsDeleted && !s.IsDeleted
                         select new { Table = t, s.TemplateVersionId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return row is null ? null : new EventMapTableInfo(row.Table, row.TemplateVersionId);
    }

    /// <inheritdoc />
    public async Task<EventMapDocumentInfo?> FindDocumentAsync(long documentId, CancellationToken ct)
        => await (from d in db.Documents.AsNoTracking()
                  join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                  where d.Id == documentId
                  select new EventMapDocumentInfo(d.ProjectId, p.TemplateVersionId))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, ColumnDef>> FindColumnsAsync(
        IReadOnlyCollection<int> ids, CancellationToken ct)
        => await db.ColumnDefs
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id) && !c.IsDeleted)
            .ToDictionaryAsync(c => c.Id, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, int>> FindRegistryEntryDefsAsync(
        IReadOnlyCollection<long> entryIds, CancellationToken ct)
        => await db.RegistryEntries
            .AsNoTracking()
            .Where(e => entryIds.Contains(e.Id) && !e.IsDeleted)
            .ToDictionaryAsync(e => e.Id, e => e.RegistryDefId, ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<SourceEventMap?> FindMapAsync(int id, CancellationToken ct)
        => db.SourceEventMaps
            .Include(m => m.Fields)
            .ThenInclude(f => f.Values)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SourceEventMap>> ListMapsAsync(int? sourceEntityId, CancellationToken ct)
        => await db.SourceEventMaps
            .AsNoTracking()
            .Include(m => m.Fields)
            .ThenInclude(f => f.Values)
            .Where(m => sourceEntityId == null || m.SourceEntityId == sourceEntityId)
            .OrderBy(m => m.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<bool> MapExistsAsync(int sourceEntityId, long documentId, int tableDefId, CancellationToken ct)
        => db.SourceEventMaps.AnyAsync(
            m => m.SourceEntityId == sourceEntityId && m.DocumentId == documentId && m.TableDefId == tableDefId, ct);

    /// <inheritdoc />
    public async Task<SourceEventMap> AddMapAsync(SourceEventMap map, CancellationToken ct)
    {
        db.SourceEventMaps.Add(map);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return map;
    }

    /// <inheritdoc />
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    /// <inheritdoc />
    public Task<int> CountLinksAsync(int mapId, CancellationToken ct)
        => db.SourceEventLinks.CountAsync(l => l.SourceEventMapId == mapId, ct);

    /// <inheritdoc />
    public void ReleaseFields(SourceEventMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        // ⚠ Спершу відповідності, потім поля: усі FK — Restrict, тож порядок видалення тримає залежність.
        foreach (var field in map.Fields)
        {
            db.RemoveRange(field.Values);
        }

        db.RemoveRange(map.Fields);
    }

    /// <inheritdoc />
    public async Task RemoveMapAsync(SourceEventMap map, CancellationToken ct)
    {
        ReleaseFields(map);
        db.SourceEventMaps.Remove(map);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> HasActiveMapAsync(int sourceEntityId, CancellationToken ct)
        => db.SourceEventMaps.AnyAsync(m => m.SourceEntityId == sourceEntityId && m.IsActive, ct);

    /// <inheritdoc />
    public async Task<SourceEventLinkPage> ReadLinksAsync(
        SourceEventLinkFilter filter, string? cursor, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = from l in db.SourceEventLinks.AsNoTracking()
                    join m in db.SourceEventMaps.AsNoTracking() on l.SourceEventMapId equals m.Id
                    join d in db.Documents.AsNoTracking() on m.DocumentId equals d.Id
                    join p in db.Projects.AsNoTracking() on d.ProjectId equals p.Id
                    where filter.MapIds.Contains(l.SourceEventMapId)
                    select new { Link = l, m.DocumentId, d.BusinessKey, d.ProjectId, p.TimeZoneId };

        if (filter.Statuses.Count > 0)
        {
            var statuses = filter.Statuses.ToList();
            query = query.Where(x => statuses.Contains(x.Link.Status));
        }

        if (filter.FromUtc is { } from)
        {
            query = query.Where(x => x.Link.StartUtc >= from);
        }

        if (filter.ToUtc is { } to)
        {
            query = query.Where(x => x.Link.StartUtc < to);
        }

        if (filter.PeriodKey is { } period)
        {
            query = query.Where(x => x.Link.PeriodKey == period);
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);

        // ⚠ Новіші за початком події першими; Id — розв'язка рівних початків, щоб курсор був однозначним.
        var page = query.OrderByDescending(x => x.Link.StartUtc).ThenByDescending(x => x.Link.Id).AsQueryable();
        if (Decode(cursor) is var (ticks, id))
        {
            var start = new DateTime(ticks, DateTimeKind.Utc);
            page = page.Where(x => x.Link.StartUtc < start || (x.Link.StartUtc == start && x.Link.Id < id));
        }

        var rows = await page.Take(limit + 1).ToListAsync(ct).ConfigureAwait(false);

        return new SourceEventLinkPage(
            [.. rows.Select(x => new SourceEventLinkRow(x.Link, x.DocumentId, x.BusinessKey, x.ProjectId, x.TimeZoneId))],
            total);
    }

    /// <inheritdoc />
    public string NextCursor(SourceEventLink last)
    {
        ArgumentNullException.ThrowIfNull(last);

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{last.StartUtc.Ticks}:{last.Id}")));
    }

    // Зіпсований курсор — початок списку, а не помилка (той самий вибір, що в `Cursor.Decode`).
    private static (long Ticks, long Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[96];
        if (!Convert.TryFromBase64String(cursor, buffer, out var written))
        {
            return null;
        }

        var parts = Encoding.UTF8.GetString(buffer[..written]).Split(':');

        return parts.Length == 2
               && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
               && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
               && ticks > 0 && ticks <= DateTime.MaxValue.Ticks
            ? (ticks, id)
            : null;
    }
}
