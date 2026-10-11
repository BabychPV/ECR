// src/Ecr.Infrastructure/Persistence/EditAssignmentManagementStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IEditAssignmentManagementStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// ⚠ Блокування — <c>UPDLOCK</c> на рядку передачі (<c>FromSql</c>, як у <c>DocumentKeyStore</c>); пошук за
/// ключем зрізу додає <c>HOLDLOCK</c>, щоб діапазонне блокування по <c>UQ_EditAssignment</c> серіалізувало
/// двох, хто одночасно створює ту саму передачу (виграє один, другий бачить її й іде гілкою «зміна»).
/// </remarks>
public sealed class EditAssignmentManagementStore(EcrDbContext db) : IEditAssignmentManagementStore
{
    private static readonly string LimitLockWaitSql = "SET LOCK_TIMEOUT " + LockWaitGuard.LockTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public async Task<EditAssignmentDocumentRef?> FindDocumentAsync(long documentId, CancellationToken ct)
        => await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
            .Join(db.Projects.AsNoTracking(), d => d.ProjectId, p => p.Id, (d, p) => new EditAssignmentDocumentRef(
                d.Id, d.ProjectId, d.BusinessKey, d.CreatedByUserId, p.Status, p.IsArchiving))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<bool> LockDocumentAsync(long documentId, CancellationToken ct)
        => (await db.Documents
            .FromSql($"SELECT * FROM doc.Document WITH (UPDLOCK, ROWLOCK) WHERE Id = {documentId}")
            .Select(d => d.Id)
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false)).Count > 0;

    /// <inheritdoc />
    public Task LimitLockWaitAsync(CancellationToken ct)
        => db.Database.ExecuteSqlRawAsync(LimitLockWaitSql, ct);

    /// <inheritdoc />
    public async Task LockRecipientsAsync(long assignmentId, CancellationToken ct)
    {
        // ⛔ N4: порядок блокувань — sheet-gate → рядок передачі → рядки виконавців. Перехід бере X на передачі
        // ПЕРШИМ (без зміни рядка: XLOCK, а не UPDATE — версія не бампається на ідемпотентних повторах), і лише
        // потім U на виконавцях. Запис комірки (EA-B) читає ті самі таблиці в тому самому порядку під S; якби
        // перехід узяв X на виконавці раніше, ніж на передачі, вийшов би цикл «запис тримає S(передача) і чекає
        // S(виконавець); перехід тримає X(виконавець) і чекає X(передача)».
        _ = await db.EditAssignments
            .FromSql($"SELECT * FROM doc.EditAssignment WITH (XLOCK, ROWLOCK) WHERE Id = {assignmentId}")
            .AsNoTracking()
            .Select(a => a.Id)
            .Take(1)
            .ToListAsync(ct).ConfigureAwait(false);

        _ = await db.EditAssignmentRecipients
            .FromSql($"SELECT * FROM doc.EditAssignmentRecipient WITH (UPDLOCK, ROWLOCK) WHERE AssignmentId = {assignmentId}")
            .AsNoTracking()
            .Select(r => r.UserId)
            .Take(EditAssignment.MaxRecipients)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<EditAssignment?> FindBySliceKeyForUpdateAsync(long documentId, string sliceKey, CancellationToken ct)
        => db.EditAssignments
            .FromSql($"SELECT * FROM doc.EditAssignment WITH (UPDLOCK, HOLDLOCK) WHERE DocumentId = {documentId} AND SliceKey = {sliceKey}")
            .Include(a => a.Recipients)
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, EditAssignmentUserRef>> GetUsersAsync(
        IReadOnlyCollection<int> userIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return new Dictionary<int, EditAssignmentUserRef>();
        }

        var ids = userIds.Distinct().ToList();
        var rows = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new EditAssignmentUserRef(u.Id, u.DisplayName, u.Email, u.IsActive))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(u => u.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EditAssignmentUserRef>> SearchCandidatesAsync(
        string query, int take, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive && !u.IsBootstrapAdmin
                && u.DisplayName.Contains(query))
            .OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .Take(take)
            .Select(u => new EditAssignmentUserRef(u.Id, u.DisplayName, null, true))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MyEditAssignmentRow>> ListMineAsync(
        int userId, long? documentId, CancellationToken ct)
    {
        var rows = await (
            from a in db.EditAssignments.AsNoTracking()
            join d in db.Documents.AsNoTracking() on a.DocumentId equals d.Id
            where (documentId == null || a.DocumentId == documentId)
                && (a.DelegatedByUserId == userId
                    || d.CreatedByUserId == userId
                    || db.EditAssignmentRecipients.Any(r => r.AssignmentId == a.Id && r.UserId == userId))
            select new
            {
                a.Id,
                a.DocumentId,
                d.ProjectId,
                d.BusinessKey,
                d.CreatedByUserId,
                a.SheetCode,
                a.TableCode,
                a.MonthNumber,
                a.PeriodKeyValue,
                a.IsExclusive,
                a.Note,
                a.DelegatedByUserId,
                a.ValidationStatus,
                a.RowVersion,
                Recipients = a.Recipients
                    .Select(r => new { r.UserId, r.Status, r.ReturnReason, r.StatusChangedAt })
                    .ToList(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var delegators = rows.Select(r => r.DelegatedByUserId).Distinct().ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => delegators.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct).ConfigureAwait(false);

        return [.. rows
            .Select(r =>
            {
                var mine = r.Recipients.FirstOrDefault(x => x.UserId == userId);
                return new MyEditAssignmentRow(
                    r.Id, r.DocumentId, r.ProjectId, r.BusinessKey,
                    r.SheetCode, r.TableCode, r.MonthNumber, r.PeriodKeyValue,
                    r.IsExclusive, r.Note,
                    r.DelegatedByUserId, names.GetValueOrDefault(r.DelegatedByUserId, string.Empty),
                    r.ValidationStatus,
                    mine?.Status,
                    // Причина повернення — лише власна: чужі рядки виконавців сюди не потрапляють.
                    mine?.ReturnReason,
                    mine?.StatusChangedAt,
                    IsOwnerOf: r.DelegatedByUserId == userId || r.CreatedByUserId == userId,
                    r.Recipients.Count,
                    r.Recipients.Count(x => x.Status == EditAssignmentRecipientStatus.Filled),
                    r.RowVersion);
            })
            .OrderBy(r => r.DocumentId).ThenBy(r => r.AssignmentId)];
    }
}
