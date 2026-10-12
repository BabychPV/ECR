using Ecr.Application.Ports;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IEditAssignmentStore"/> над <see cref="EcrDbContext"/> (EA-1).</summary>
/// <remarks>
/// ⚠ Без власного <c>SaveChanges</c>: запис разом з аудитом і сповіщеннями йде однією транзакцією
/// обробника (<see cref="IUnitOfWork"/>).
/// </remarks>
public sealed class EditAssignmentStore(EcrDbContext db) : IEditAssignmentStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<EditAssignment>> ListByDocumentAsync(long documentId, CancellationToken ct)
        => await db.EditAssignments
            .Include(a => a.Recipients)
            .Where(a => a.DocumentId == documentId)
            .OrderBy(a => a.Id)
            .Take(EditAssignment.MaxPerDocument)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<EditAssignment?> FindAsync(long documentId, long assignmentId, CancellationToken ct)
        => db.EditAssignments
            .Include(a => a.Recipients)
            .FirstOrDefaultAsync(a => a.DocumentId == documentId && a.Id == assignmentId, ct);

    /// <inheritdoc />
    public Task<EditAssignment?> FindForUpdateAsync(long documentId, long assignmentId, CancellationToken ct)
        => db.EditAssignments
            .FromSql($"SELECT * FROM doc.EditAssignment WITH (UPDLOCK, ROWLOCK) WHERE DocumentId = {documentId} AND Id = {assignmentId}")
            .Include(a => a.Recipients)
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc />
    public async Task<EditAssignmentGateFacts> ReadFactsForGateAsync(long documentId, int userId, CancellationToken ct)
    {
        var read = await EditAssignmentFactsReader.LoadAsync(db, [documentId], userId, forUpdate: true, ct)
            .ConfigureAwait(false);

        return read.TryGetValue(documentId, out var found)
            ? new EditAssignmentGateFacts(found.OwnerUserId, found.Facts)
            : new EditAssignmentGateFacts(0, []);
    }

    /// <inheritdoc />
    public Task<EditAssignment?> FindBySliceKeyAsync(long documentId, string sliceKey, CancellationToken ct)
        => db.EditAssignments
            .Include(a => a.Recipients)
            .FirstOrDefaultAsync(a => a.DocumentId == documentId && a.SliceKey == sliceKey, ct);

    /// <inheritdoc />
    public Task<int> CountByDocumentAsync(long documentId, CancellationToken ct)
        => db.EditAssignments.CountAsync(a => a.DocumentId == documentId, ct);

    /// <inheritdoc />
    public void Add(EditAssignment assignment) => db.EditAssignments.Add(assignment);

    /// <inheritdoc />
    public void Remove(EditAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        // Без каскаду: виконавців — раніше за передачу.
        db.EditAssignmentRecipients.RemoveRange(assignment.Recipients);
        db.EditAssignments.Remove(assignment);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EditAssignmentTaskRow>> ListTasksAsync(
        int userId,
        IReadOnlyCollection<EditAssignmentRecipientStatus> statuses,
        long? documentId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        var rows = await db.EditAssignmentRecipients.AsNoTracking()
            .Where(r => r.UserId == userId && statuses.Contains(r.Status))
            .Join(db.EditAssignments.AsNoTracking(), r => r.AssignmentId, a => a.Id, (r, a) => new { r, a })
            .Where(x => documentId == null || x.a.DocumentId == documentId)
            .OrderByDescending(x => x.r.StatusChangedAt)
            .Select(x => new
            {
                x.a.Id,
                x.a.DocumentId,
                x.a.SheetCode,
                x.a.TableCode,
                x.a.MonthNumber,
                x.a.PeriodKeyValue,
                x.r.Status,
                x.r.ReturnReason,
                x.a.Note,
                x.a.DelegatedByUserId,
                x.a.ExpiresAt,
                x.r.StatusChangedAt,
            })
            .Take(500)
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. rows.Select(x => new EditAssignmentTaskRow(
            x.Id,
            x.DocumentId,
            EditAssignmentSlice.Create(x.SheetCode, x.TableCode, x.MonthNumber, x.PeriodKeyValue),
            x.Status,
            x.ReturnReason,
            x.Note,
            x.DelegatedByUserId,
            x.ExpiresAt,
            x.StatusChangedAt))];
    }
}

/// <summary>Реалізація <see cref="IEditorSetStore"/> над <see cref="EcrDbContext"/> (EA-1).</summary>
public sealed class EditorSetStore(EcrDbContext db) : IEditorSetStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<EditorSet>> ListByOwnerAsync(int ownerUserId, CancellationToken ct)
        => await db.EditorSets
            .Include(s => s.Members)
            .Where(s => s.OwnerUserId == ownerUserId)
            .OrderBy(s => s.Name)
            .Take(EditorSet.MaxPerOwner)
            .ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<EditorSet?> FindAsync(int ownerUserId, int setId, CancellationToken ct)
        => db.EditorSets
            .Include(s => s.Members)
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerUserId && s.Id == setId, ct);

    /// <inheritdoc />
    public Task<int> CountByOwnerAsync(int ownerUserId, CancellationToken ct)
        => db.EditorSets.CountAsync(s => s.OwnerUserId == ownerUserId, ct);

    /// <inheritdoc />
    public Task<bool> NameExistsAsync(int ownerUserId, string name, int? exceptSetId, CancellationToken ct)
        => db.EditorSets.AnyAsync(
            s => s.OwnerUserId == ownerUserId && s.Name == name && (exceptSetId == null || s.Id != exceptSetId), ct);

    /// <inheritdoc />
    public void Add(EditorSet set) => db.EditorSets.Add(set);

    /// <inheritdoc />
    public async Task RemoveAsync(EditorSet set, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(set);

        await db.EditAssignments
            .Where(a => a.AppliedSetId == set.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.AppliedSetId, (int?)null), ct).ConfigureAwait(false);

        db.EditorSetMembers.RemoveRange(set.Members);
        db.EditorSets.Remove(set);
    }
}
