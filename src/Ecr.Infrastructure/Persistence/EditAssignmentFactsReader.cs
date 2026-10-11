// src/Ecr.Infrastructure/Persistence/EditAssignmentFactsReader.cs
using Ecr.Application.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Читає передачі документів у вигляді фактів для <see cref="EditAssignmentGuard"/> (EA-B):
/// ОДНЕ місце проєкції для рішення про доступ і для повторної перевірки під блокуванням аркуша.
/// </summary>
/// <remarks>
/// ⛔ Передачі читаються лише З БАЗИ, на кожне рішення (профіль доступу кешується й не знає
/// документа). Рядок містить лише стан САМОГО користувача — стани й імена інших виконавців
/// правилу не потрібні й не виходять назовні (R9).
/// </remarks>
internal static class EditAssignmentFactsReader
{
    /// <summary>Передачі документів для користувача: документ → автор і факти.</summary>
    /// <param name="db">Контекст.</param>
    /// <param name="documentIds">Документи; для <paramref name="forUpdate"/> — рівно один.</param>
    /// <param name="userId">Для чийого стану виконавця будуються факти.</param>
    /// <param name="forUpdate">
    /// <c>true</c> — рядки передач беруться під <c>UPDLOCK, ROWLOCK</c> (повторна перевірка в
    /// транзакції запису: перехід <c>Fill/Validate/Return</c> чекає її завершення).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<IReadOnlyDictionary<long, (int OwnerUserId, IReadOnlyList<EditAssignmentFact> Facts)>>
        LoadAsync(
            EcrDbContext db, IReadOnlyCollection<long> documentIds, int userId, bool forUpdate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(documentIds);

        var result = new Dictionary<long, (int, IReadOnlyList<EditAssignmentFact>)>();
        if (documentIds.Count == 0)
        {
            return result;
        }

        IQueryable<EditAssignment> source;
        if (forUpdate)
        {
            if (documentIds.Count != 1)
            {
                throw new ArgumentException("Блокувальне читання передач — по одному документу.", nameof(documentIds));
            }

            var documentId = documentIds.First();
            source = db.EditAssignments
                .FromSql($"SELECT * FROM doc.EditAssignment WITH (UPDLOCK, ROWLOCK) WHERE DocumentId = {documentId}")
                .AsNoTracking();
        }
        else
        {
            source = db.EditAssignments.AsNoTracking().Where(a => documentIds.Contains(a.DocumentId));
        }

        var rows = await (
                from a in source
                join d in db.Documents.AsNoTracking() on a.DocumentId equals d.Id
                join u in db.Users.AsNoTracking() on a.DelegatedByUserId equals u.Id
                select new
                {
                    a.DocumentId,
                    a.SheetCode,
                    a.TableCode,
                    a.MonthNumber,
                    a.PeriodKeyValue,
                    a.IsExclusive,
                    a.ValidationStatus,
                    a.DelegatedByUserId,
                    a.ExpiresAt,
                    DelegatorActive = u.IsActive,
                    OwnerUserId = d.CreatedByUserId,
                    Mine = db.EditAssignmentRecipients
                        .Where(r => r.AssignmentId == a.Id && r.UserId == userId)
                        .Select(r => (EditAssignmentRecipientStatus?)r.Status)
                        .FirstOrDefault(),
                })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var group in rows.GroupBy(r => r.DocumentId))
        {
            var facts = group
                .Select(r => new EditAssignmentFact(
                    r.SheetCode,
                    r.TableCode,
                    r.MonthNumber,
                    r.PeriodKeyValue is { } p ? new PeriodKey(p) : null,
                    r.IsExclusive,
                    r.ValidationStatus == EditAssignmentValidationStatus.Validated,
                    r.DelegatorActive,
                    r.DelegatedByUserId,
                    r.ExpiresAt,
                    r.Mine is { } status ? ToProgress(status) : null))
                .ToList();

            result[group.Key] = (group.First().OwnerUserId, facts);
        }

        return result;
    }

    private static AssigneeProgress ToProgress(EditAssignmentRecipientStatus status) => status switch
    {
        EditAssignmentRecipientStatus.Filled => AssigneeProgress.Filled,
        EditAssignmentRecipientStatus.Returned => AssigneeProgress.Returned,
        _ => AssigneeProgress.Assigned,
    };
}
