using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>✎ B75: реалізація <see cref="IRowLockStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <param name="db">Контекст бази.</param>
public sealed class RowLockStore(EcrDbContext db) : IRowLockStore
{
    /// <summary>Правило ефективного стану — те саме, що в рішенні про запис (F-08).</summary>
    private static readonly PeriodStateCalculator PeriodStates = new();

    /// <inheritdoc />
    public async Task<RowLockTarget?> GetTargetAsync(long documentId, int periodKey, DateTime utcNow, CancellationToken ct)
    {
        // Один запит: документ → проєкт, період і виняток документа — підзапитами (як `DocumentPeriodReopenStore`).
        var row = await (
                from document in db.Documents.AsNoTracking()
                where document.Id == documentId
                join project in db.Projects.AsNoTracking() on document.ProjectId equals project.Id
                select new
                {
                    project.Id,
                    document.CreatedByUserId,
                    project.Status,
                    project.IsArchiving,
                    TemplateVersionId = document.TemplateId == null
                        ? EcrDbContext.VersionOf(document.ProjectId, periodKey)
                        : EcrDbContext.TemplateVersionOf(document.ProjectId, document.TemplateId.Value, periodKey),
                    project.TimeZoneId,
                    project.PeriodEnd,
                    project.YearGraceOffsetDays,
                    document.BusinessKey,
                    Period = db.Periods
                        .AsNoTracking()
                        .Where(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == periodKey)
                        .FirstOrDefault(),
                    Reopened = db.DocumentPeriodReopens.Any(x => x.DocumentId == documentId
                                                                 && x.PeriodKeyValue == periodKey
                                                                 && x.ClosedAt == null
                                                                 && x.OpenUntil > utcNow),
                })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        // ⚠ Ефективний стан лише для АКТИВНОГО проєкту — як у рішенні про запис (`A7-25`); виняток документа піднімає
        // Closed → Grace лише для нього (`DocumentPeriodReopenRule`).
        PeriodState? state = row.Period is null
            ? null
            : DocumentPeriodReopenRule.Apply(
                row.Status == ProjectStatus.Active
                    ? PeriodStates.Effective(
                        row.Period,
                        utcNow,
                        YearGraceWindow.For(
                            row.PeriodEnd, row.YearGraceOffsetDays, SiteTimeZone.Create(row.TimeZoneId).ToTimeZoneInfo()))
                    : row.Period.State,
                row.Reopened);

        return new RowLockTarget(
            row.Id, row.CreatedByUserId, row.Status, row.IsArchiving, state, row.TimeZoneId, row.TemplateVersionId,
            row.BusinessKey);
    }

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> ExistingRowKeysAsync(
        long documentId, int periodKey, long? sheetCopyId, int tableDefId, IReadOnlyCollection<string> rowKeys,
        CancellationToken ct)
    {
        var keys = rowKeys.ToList();

        // ⛔ WR-05: предикат партиції (`PeriodKey`) першим — і в екземплярі, і в рядках.
        var found = await (
                from t in db.TableInstances.AsNoTracking()
                where t.PeriodKeyValue == periodKey
                      && t.DocumentId == documentId
                      && t.TableDefId == tableDefId
                      && t.SheetCopyId == sheetCopyId
                join r in db.TableRows.AsNoTracking() on t.Id equals r.TableInstanceId
                where r.PeriodKeyValue == periodKey && !r.IsDeleted && keys.Contains(r.RowKeyValue)
                select r.RowKeyValue)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return found.ToHashSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> OverlappingAsync(
        long documentId, int periodKey, long? sheetCopyId, string sheetCode, string tableCode,
        IReadOnlyCollection<string> rowKeys, DateTime from, DateTime until, DateTime utcNow, CancellationToken ct)
    {
        var keys = rowKeys.ToList();
        return await db.RowLocks
            .AsNoTracking()
            .Where(x => x.DocumentId == documentId
                        && x.PeriodKeyValue == periodKey
                        && x.ReleasedAt == null
                        && x.SheetCopyId == sheetCopyId
                        && x.SheetCode == sheetCode
                        && x.TableCode == tableCode
                        && keys.Contains(x.RowKey)
                        && x.LockUntil > utcNow // F13 D3: прострочене незняте блокування — історія, не конфлікт
                        && x.LockFrom < until
                        && x.LockUntil > from)
            .Select(x => x.RowKey)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RowLockRecord>> ListAsync(
        long documentId, int periodKey, DateTime utcNow, CancellationToken ct)
        => await (from x in db.RowLocks.AsNoTracking()
                  where x.DocumentId == documentId
                        && x.PeriodKeyValue == periodKey
                        && x.ReleasedAt == null
                        && x.LockUntil > utcNow
                  join u in db.Users.AsNoTracking() on x.OwnerUserId equals u.Id into users
                  from u in users.DefaultIfEmpty()
                  orderby x.SheetCode, x.TableCode, x.RowKey, x.LockFrom
                  select new RowLockRecord(
                      x.Id, x.SheetCopyId, x.SheetCode, x.TableCode, x.RowKey, x.LockFrom, x.LockUntil, x.OwnerUserId,
                      u == null ? null : u.DisplayName, x.Note))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public Task<RowLock?> FindAsync(long documentId, long lockId, CancellationToken ct)
        => db.RowLocks.FirstOrDefaultAsync(x => x.Id == lockId && x.DocumentId == documentId, ct);

    /// <inheritdoc />
    public async Task LockDocumentAsync(long documentId, CancellationToken ct)
        => _ = await db.Documents
            .FromSql($"SELECT * FROM doc.Document WITH (UPDLOCK, ROWLOCK) WHERE Id = {documentId}")
            .OrderBy(d => d.Id)
            .Select(d => d.Id)
            .Take(1)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<RowLockKey>> LockedByOthersAsync(
        long documentId, int periodKey, int userId, DateTime utcNow, CancellationToken ct)
    {
        var rows = await db.RowLocks
            .AsNoTracking()
            .Where(x => x.DocumentId == documentId
                        && x.PeriodKeyValue == periodKey
                        && x.ReleasedAt == null
                        && x.LockFrom <= utcNow
                        && x.LockUntil > utcNow
                        && x.OwnerUserId != userId)
            .Select(x => new { x.SheetCopyId, x.SheetCode, x.TableCode, x.RowKey })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. rows.Select(x => new RowLockKey(x.SheetCopyId, x.SheetCode, x.TableCode, x.RowKey))];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RowLock>> FindUnreleasedForRowAsync(
        long documentId, int periodKey, RowLockKey key, CancellationToken ct)
        => await db.RowLocks
            .Where(x => x.DocumentId == documentId
                        && x.PeriodKeyValue == periodKey
                        && x.ReleasedAt == null
                        && x.SheetCopyId == key.SheetCopyId
                        && x.SheetCode == key.SheetCode
                        && x.TableCode == key.TableCode
                        && x.RowKey == key.RowKey)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public void AddRange(IEnumerable<RowLock> locks) => db.RowLocks.AddRange(locks);
}
