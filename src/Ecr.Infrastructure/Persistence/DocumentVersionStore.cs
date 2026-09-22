using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IDocumentVersionStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class DocumentVersionStore(EcrDbContext db) : IDocumentVersionStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentVersionRecord>> ListAsync(
        long documentId, PeriodKey periodKey, int max, CancellationToken ct)
        => await (
            from s in db.SubmissionSnapshots.AsNoTracking()
            where s.DocumentId == documentId && s.PeriodKey == periodKey.Value
            join u in db.Users.AsNoTracking() on s.SubmittedByUserId equals u.Id into users
            from u in users.DefaultIfEmpty()
            orderby s.SubmittedAt descending, s.Id descending
            select new DocumentVersionRecord(
                s.Id, s.SheetDefId, s.PeriodKey, s.SubmittedAt, s.SubmittedByUserId,
                u == null ? null : u.DisplayName))
            .Take(max)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<DocumentVersionPayload?> FindAsync(long documentId, long versionId, CancellationToken ct)
        => await db.SubmissionSnapshots.AsNoTracking()
            .Where(s => s.Id == versionId && s.DocumentId == documentId)
            .Select(s => new DocumentVersionPayload(s.Id, s.PeriodKey, s.PayloadJson))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<VersionCell>> ReadCurrentAsync(
        long documentId, PeriodKey periodKey, CancellationToken ct)
    {
        var p = periodKey.Value;
        var cells = await (
            from instance in db.TableInstances.AsNoTracking()
            where instance.DocumentId == documentId && instance.PeriodKeyValue == p
            join row in db.TableRows.AsNoTracking()
                on new { P = instance.PeriodKeyValue, I = instance.Id }
                equals new { P = row.PeriodKeyValue, I = row.TableInstanceId }
            join cell in db.CellValues.AsNoTracking()
                on new { P = row.PeriodKeyValue, R = row.Id }
                equals new { P = cell.PeriodKeyValue, R = cell.TableRowId }
            where !row.IsDeleted
            select new CellRow(cell.TableRowId, cell.ColumnDefId, cell.ValueNumeric, cell.ValueString))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Та сама форма значення, що пише SubmitSheetHandler у зріз.
        return [.. cells.Select(c => new VersionCell(
            c.RowId, c.ColumnDefId,
            c.Numeric?.ToString(CultureInfo.InvariantCulture) ?? c.Text))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<long, RowLabel>> DescribeRowsAsync(
        PeriodKey periodKey, IReadOnlyCollection<long> rowIds, CancellationToken ct)
    {
        if (rowIds.Count == 0)
        {
            return new Dictionary<long, RowLabel>();
        }

        var p = periodKey.Value;
        var ids = rowIds.ToList();
        var rows = await (
            from row in db.TableRows.AsNoTracking()
            join instance in db.TableInstances.AsNoTracking()
                on new { P = row.PeriodKeyValue, I = row.TableInstanceId }
                equals new { P = instance.PeriodKeyValue, I = instance.Id }
            join table in db.TableDefs.AsNoTracking() on instance.TableDefId equals table.Id
            where row.PeriodKeyValue == p && ids.Contains(row.Id)
            select new LabelRow(row.Id, table.Code, row.RowKeyValue))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.RowId, r => new RowLabel(r.TableCode, r.RowKey));
    }

    private sealed record CellRow(long RowId, int ColumnDefId, decimal? Numeric, string? Text);

    private sealed record LabelRow(long RowId, string TableCode, string RowKey);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, string>> ColumnCodesAsync(
        IReadOnlyCollection<int> columnDefIds, CancellationToken ct)
    {
        var ids = columnDefIds.ToList();
        return ids.Count == 0
            ? new Dictionary<int, string>()
            : await db.ColumnDefs.AsNoTracking()
                .Where(c => ids.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Code, ct)
                .ConfigureAwait(false);
    }
}
