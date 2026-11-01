// src/Ecr.Infrastructure/Persistence/ApprovalQueueStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IApprovalQueueStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class ApprovalQueueStore(EcrDbContext db) : IApprovalQueueStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ApprovalQueueProject>> ListProjectsAsync(CancellationToken ct)
        => await (
                from p in db.Projects.AsNoTracking()
                join v in db.TemplateVersions.AsNoTracking() on p.TemplateVersionId equals v.Id
                join t in db.Templates.AsNoTracking() on v.TemplateId equals t.Id
                where p.Status != ProjectStatus.Archived && !p.IsArchiving
                select new ApprovalQueueProject(
                    p.Id, p.Code, p.TemplateVersionId, t.Code, p.Status, p.IsArchiving,
                    // ✎ RPT-STABLE B2: проєкт має періоди на інших версіях (шкала) - тим самим запитом, без зайвого звернення.
                    db.ProjectTemplateVersions.Any(x => x.ProjectId == p.Id && x.TemplateVersionId != p.TemplateVersionId)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<ApprovalQueueRoutes> ListActiveRoutesAsync(CancellationToken ct)
    {
        var routes = await db.ApprovalRoutes
            .AsNoTracking()
            .Include(r => r.Steps)
            .Where(r => r.IsActive)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var roleIds = routes.SelectMany(r => r.Steps).Select(s => s.RoleId).Distinct().ToList();
        var codes = roleIds.Count == 0
            ? []
            : await db.Roles
                .AsNoTracking()
                .Where(r => roleIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Code })
                .ToDictionaryAsync(r => r.Id, r => r.Code, ct)
                .ConfigureAwait(false);

        return new ApprovalQueueRoutes(routes, codes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ ОДИН запит на порцію (ім'я того, хто подав, — підзапитом у тому ж зверненні): без N+1. Порядок
    /// «момент подання, ключ стану» детермінований; курсор — keyset, а не зсув, тож нове подання не зсуває
    /// вже прочитане.
    /// </remarks>
    public async Task<IReadOnlyList<ApprovalQueueCandidate>> ListSubmittedAsync(
        IReadOnlyCollection<int> projectIds, bool requireIncludedSheet, ApprovalQueueCursor? after, int take,
        IReadOnlyCollection<long> participatedDocumentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        ArgumentNullException.ThrowIfNull(participatedDocumentIds);

        // Масив, а не інтерфейс: `Contains` над масивом EF перекладає в `IN (...)`.
        var participated = participatedDocumentIds.ToArray();
        if (projectIds.Count == 0 && participated.Length == 0)
        {
            return [];
        }

        var submitted =
            from a in db.ApprovalStates.AsNoTracking()
            join d in db.Documents.AsNoTracking() on a.DocumentId equals d.Id
            where a.Status == DocumentStatus.Submitted
            select new { a, d };

        // ✎ D-444 (AN-131): документи учасника — ДОДАТКОВО до проєктів; без надань — попередній предикат.
        submitted = participated.Length == 0
            ? submitted.Where(x => projectIds.Contains(x.d.ProjectId))
            : submitted.Where(x => projectIds.Contains(x.d.ProjectId) || participated.Contains(x.d.Id));

        var query = submitted.Select(x => new
        {
            x.a.Id,
            x.a.DocumentId,
            x.d.ProjectId,
            x.d.BusinessKey,
            x.a.SheetDefId,
            x.a.PeriodKey,
            SubmittedAt = x.a.SubmittedAt ?? DateTime.MinValue,
            x.a.SubmittedByUserId,
            x.a.CurrentStepId,
        });

        if (requireIncludedSheet)
        {
            query = query.Where(x => db.DocumentSheets.Any(
                s => s.DocumentId == x.DocumentId && s.SheetDefId == x.SheetDefId && s.IsIncluded));
        }

        if (after is not null)
        {
            var at = new DateTime(after.SubmittedAtTicks, DateTimeKind.Utc);
            var id = after.StateId;
            query = query.Where(x => x.SubmittedAt > at || (x.SubmittedAt == at && x.Id > id));
        }

        var page = await query
            .OrderBy(x => x.SubmittedAt)
            .ThenBy(x => x.Id)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.DocumentId,
                x.ProjectId,
                x.BusinessKey,
                x.SheetDefId,
                x.PeriodKey,
                x.SubmittedAt,
                x.SubmittedByUserId,
                x.CurrentStepId,
                SubmittedByDisplayName = db.Users
                    .Where(u => u.Id == x.SubmittedByUserId)
                    .Select(u => u.DisplayName)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return
        [
            .. page.Select(x => new ApprovalQueueCandidate(
                x.Id,
                x.DocumentId,
                x.ProjectId,
                x.BusinessKey,
                x.SheetDefId,
                x.PeriodKey,
                DateTime.SpecifyKind(x.SubmittedAt, DateTimeKind.Utc),
                x.SubmittedByUserId,
                x.SubmittedByDisplayName,
                x.CurrentStepId)),
        ];
    }

    /// <inheritdoc />
    /// <remarks>
    /// ✎ D-449. ⚠ ОДИН запит на порцію (ім'я того, хто повернув, — підзапитом у тому ж зверненні): без N+1.
    /// <para>
    /// Два види повернення (рішення координатора, узгоджено з поштовими подіями lane C):
    /// <list type="bullet">
    /// <item><c>Rejected</c> — відхилення погоджувачем: хто/коли — <c>ApprovedByUserId</c>/<c>ApprovedAt</c> (їх ставить
    /// <c>ApprovalState.Reject</c>), причина — <c>RejectedReason</c>;</item>
    /// <item><c>Draft</c>, чия ОСТАННЯ подія журналу (<c>wf.ApprovalEvent</c> аркуша й періоду) — <c>Reopen</c>: хто/коли/
    /// причина — <c>ReopenedByUserId</c>/<c>ReopenedAt</c>/<c>ReopenReason</c> стану. Це ті самі значення, що й у цій
    /// останній події: <c>ReopenDocumentHandler</c> пише їх обидва одним викликом в одній транзакції; поля стану дають
    /// keyset без підзапиту в порядку. Відкликання автором (<c>Recall</c>) теж веде в <c>Draft</c>, але останньою подією
    /// стає <c>Recall</c> — і рядок не потрапляє. Попередній фільтр <c>ReopenedAt != null</c> відсікає чернетки, яких
    /// ніколи не повертали, ще до підзапиту журналу.</item>
    /// </list>
    /// Повторне подання переводить стан у <c>Submitted</c> — рядок випадає з вибірки сам.
    /// </para>
    /// Порядок «момент повернення, ключ стану» детермінований; курсор — keyset.
    /// </remarks>
    public async Task<IReadOnlyList<ReturnedSheetCandidate>> ListReturnedAsync(
        int userId,
        IReadOnlyCollection<int> ownedProjectIds,
        bool requireIncludedSheet,
        ReturnedSheetsCursor? after,
        int take,
        IReadOnlyCollection<long> participatedDocumentIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownedProjectIds);
        ArgumentNullException.ThrowIfNull(participatedDocumentIds);

        // Масиви, а не інтерфейси: `Contains` над масивом EF перекладає в `IN (...)`.
        var projects = ownedProjectIds.ToArray();
        var participated = participatedDocumentIds.ToArray();
        if (projects.Length == 0 && participated.Length == 0)
        {
            return [];
        }

        var returned =
            from a in db.ApprovalStates.AsNoTracking()
            join d in db.Documents.AsNoTracking() on a.DocumentId equals d.Id
            where a.Status == DocumentStatus.Rejected
                  || (a.Status == DocumentStatus.Draft
                      && a.ReopenedAt != null
                      && db.ApprovalEvents
                          .Where(e => e.DocumentId == a.DocumentId && e.SheetDefId == a.SheetDefId && e.PeriodKey == a.PeriodKey)
                          .OrderByDescending(e => e.At)
                          .ThenByDescending(e => e.Id)
                          .Select(e => (ApprovalAction?)e.Action)
                          .FirstOrDefault() == ApprovalAction.Reopen)
            select new { a, d };

        // Власник чи останній подавач — у видимих за ролями проєктах; учасник — у своїх документах.
        returned = participated.Length == 0
            ? returned.Where(x => (x.d.CreatedByUserId == userId || x.a.SubmittedByUserId == userId)
                                  && projects.Contains(x.d.ProjectId))
            : returned.Where(x => ((x.d.CreatedByUserId == userId || x.a.SubmittedByUserId == userId)
                                   && projects.Contains(x.d.ProjectId))
                                  || participated.Contains(x.d.Id));

        var query = returned.Select(x => new
        {
            x.a.Id,
            x.a.DocumentId,
            x.d.ProjectId,
            x.d.BusinessKey,
            x.d.NameL10n,
            x.d.CreatedByUserId,
            x.a.SubmittedByUserId,
            x.a.SheetDefId,
            x.a.PeriodKey,
            Reopened = x.a.Status == DocumentStatus.Draft,
            ReturnedAt = (x.a.Status == DocumentStatus.Rejected ? x.a.ApprovedAt : x.a.ReopenedAt) ?? DateTime.MinValue,
            ReturnedByUserId = x.a.Status == DocumentStatus.Rejected ? x.a.ApprovedByUserId : x.a.ReopenedByUserId,
            Reason = x.a.Status == DocumentStatus.Rejected ? x.a.RejectedReason : x.a.ReopenReason,
        });

        if (requireIncludedSheet)
        {
            query = query.Where(x => db.DocumentSheets.Any(
                s => s.DocumentId == x.DocumentId && s.SheetDefId == x.SheetDefId && s.IsIncluded));
        }

        if (after is not null)
        {
            var at = new DateTime(after.ReturnedAtTicks, DateTimeKind.Utc);
            var id = after.StateId;
            query = query.Where(x => x.ReturnedAt > at || (x.ReturnedAt == at && x.Id > id));
        }

        var page = await query
            .OrderBy(x => x.ReturnedAt)
            .ThenBy(x => x.Id)
            .Take(take)
            .Select(x => new
            {
                x.Id,
                x.DocumentId,
                x.ProjectId,
                x.BusinessKey,
                x.NameL10n,
                x.CreatedByUserId,
                x.SubmittedByUserId,
                x.SheetDefId,
                x.PeriodKey,
                x.Reopened,
                x.ReturnedAt,
                x.ReturnedByUserId,
                x.Reason,
                ReturnedByDisplayName = db.Users
                    .Where(u => u.Id == x.ReturnedByUserId)
                    .Select(u => u.DisplayName)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return
        [
            .. page.Select(x => new ReturnedSheetCandidate(
                x.Id,
                x.DocumentId,
                x.ProjectId,
                x.BusinessKey,
                x.NameL10n,
                x.CreatedByUserId,
                x.SheetDefId,
                x.PeriodKey,
                DateTime.SpecifyKind(x.ReturnedAt, DateTimeKind.Utc),
                x.ReturnedByDisplayName,
                x.Reason ?? string.Empty)
            {
                SubmittedByUserId = x.SubmittedByUserId,
                ReturnedByUserId = x.ReturnedByUserId,
                Kind = x.Reopened ? ReturnKind.Reopened : ReturnKind.Rejected,
            }),
        ];
    }
}
