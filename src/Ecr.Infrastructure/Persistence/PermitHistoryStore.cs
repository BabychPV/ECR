using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary><see cref="IPermitHistoryStore"/> over <see cref="EcrDbContext"/>.</summary>
/// <param name="db">Database context.</param>
public sealed class PermitHistoryStore(EcrDbContext db) : IPermitHistoryStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitSegmentRecord>> ListAsync(long documentId, CancellationToken ct)
        => await (from s in db.PermitSegments.AsNoTracking()
                  where s.DocumentId == documentId
                  join u in db.Users.AsNoTracking() on s.CreatedByUserId equals u.Id into users
                  from u in users.DefaultIfEmpty()
                  orderby s.ValidFrom, s.Id
                  select new PermitSegmentRecord(s, u == null ? null : u.DisplayName))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitSegment>> RowSegmentsAsync(
        long documentId, int tableDefId, long? sheetCopyId, string rowKey, int columnDefId, CancellationToken ct)
        => await db.PermitSegments
            .Where(s => s.DocumentId == documentId && s.TableDefId == tableDefId && s.SheetCopyId == sheetCopyId
                        && s.RowKey == rowKey && s.ColumnDefId == columnDefId)
            .OrderBy(s => s.ValidFrom)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<PermitSegment>> HeaderSegmentsAsync(long documentId, int headerFieldDefId, CancellationToken ct)
        => await db.PermitSegments
            .Where(s => s.DocumentId == documentId && s.HeaderFieldDefId == headerFieldDefId)
            .OrderBy(s => s.ValidFrom)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<CellPermitPart>> PartsAsync(
        long documentId, IReadOnlyCollection<int> periodKeys, CancellationToken ct)
    {
        if (periodKeys.Count == 0)
        {
            return [];
        }

        return await db.CellPermitParts.AsNoTracking()
            .Where(p => periodKeys.Contains(p.PeriodKeyValue) && p.DocumentId == documentId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> RowHasPartsOfAsync(
        long documentId, int tableDefId, long? sheetCopyId, string rowKey, long registryEntryId, CancellationToken ct)
        => db.CellPermitParts.AsNoTracking().AnyAsync(
            p => p.DocumentId == documentId && p.TableDefId == tableDefId && p.SheetCopyId == sheetCopyId
                 && p.RowKey == rowKey && p.RegistryEntryId == registryEntryId,
            ct);

    /// <inheritdoc />
    public void Add(PermitSegment segment) => db.PermitSegments.Add(segment);

    /// <inheritdoc />
    public void Remove(PermitSegment segment) => db.PermitSegments.Remove(segment);
}
