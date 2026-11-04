// src/Ecr.Infrastructure/Persistence/StepApproverStore.cs
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// AN-59 (варіант C, D-466): <see cref="IStepApproverStore"/> над <see cref="EcrDbContext"/> — три звернення до бази
/// на ВЕСЬ виклик, скільки б користувачів не мала роль кроку.
/// </summary>
/// <remarks>
/// ⚠ Лише передфільтр, як і <c>SheetWorkflowMailStore</c>: роль кроку + непорожній не-Deny грант рівня ≥ Approve
/// на проєкт (або на один із поданих аркушів), призначення, активний користувач; плюс учасники документа з
/// наданням Approve. Область ролі (ScopeJson), строки дії призначення (RoleAssignment.IsEffectiveOn — єдине формулювання\n/// межі), заборони, вікна доступу — справа рішення про затвердження.
/// ⛔ Порядок кандидатів — за Id користувача (детермінований), ліміт — на рядки «користувач × роль».
/// </remarks>
public sealed class StepApproverStore(EcrDbContext db, IClock clock, IWorkflowStore workflow)
    : IStepApproverStore
{
    /// <inheritdoc />
    public async Task<StepApproverFacts?> LoadAsync(long documentId, int periodKey, int maxCandidates, CancellationToken ct)
    {
        // 1) Подані аркуші й копії документа за період + проєкт і версія шаблону ПЕРІОДУ (як у CurrentApprovalStepAsync).
        var states = await (
                from s in db.ApprovalStates.AsNoTracking()
                join d in db.Documents.AsNoTracking() on s.DocumentId equals d.Id
                join sd in db.SheetDefs.AsNoTracking() on s.SheetDefId equals sd.Id
                where s.DocumentId == documentId && s.PeriodKey == periodKey && s.Status == DocumentStatus.Submitted
                select new
                {
                    s.SheetDefId,
                    s.SheetCopyId,
                    s.CurrentStepId,
                    sd.Code,
                    d.ProjectId,
                    TemplateVersionId = EcrDbContext.VersionOf(d.ProjectId, periodKey),
                })
            .ToListAsync(ct).ConfigureAwait(false);
        if (states.Count == 0)
        {
            return null;
        }

        // 2) Маршрут — один на документ і період (версія періоду спільна для всіх його аркушів); крок кожного аркуша — за
        // його CurrentStepId доменним правилом (`StepAt`), без маршруту — роль не вимагається (одноетапне затвердження).
        var projectId = states[0].ProjectId;
        var route = await workflow.FindRouteAsync(projectId, states[0].TemplateVersionId, ct).ConfigureAwait(false);
        var hasRoute = route is not null && route.Steps.Count > 0;

        var steps = states
            .Select(s => new SubmittedStepRef(
                s.SheetDefId, s.SheetCopyId, hasRoute ? route!.StepAt(s.CurrentStepId)?.RoleId : null))
            .Where(s => !hasRoute || s.RoleId is not null)
            .ToList();
        if (steps.Count == 0)
        {
            return new StepApproverFacts([], [], Capped: false);
        }

        var stepRoleIds = steps.Where(s => s.RoleId is not null).Select(s => s.RoleId!.Value).Distinct().ToList();
        var anyOneStage = steps.Any(s => s.RoleId is null);
        var sheetIds = steps.Select(s => s.SheetDefId).Distinct().ToList();
        var sheetCodes = states.Select(s => s.Code).Distinct().ToList();
        var now = clock.UtcNow;

        // 3) Кандидати з іменами одним запитом: носії ролі кроку (або, без маршруту, будь-якої ролі з грантом Approve)
        // ∪ учасники документа з наданням Approve. Рядки «користувач × роль» (RoleId = null — учасник), ліміт + 1,
        // щоб побачити обрізання.
        var approveGrants = db.ResourceGrants.AsNoTracking()
            .Where(g => !g.IsDeny && g.Level >= GrantLevel.Approve
                        && ((g.ResourceKind == ResourceKind.Project && g.ResourceId == projectId)
                            || (g.ResourceKind == ResourceKind.Sheet && sheetIds.Contains(g.ResourceId))));

        var holders =
            from a in db.RoleAssignments.AsNoTracking()
            join u in db.Users.AsNoTracking() on a.UserId equals u.Id
            where u.IsActive
                  && (stepRoleIds.Contains(a.RoleId) || anyOneStage)
                  && approveGrants.Any(g => g.RoleId == a.RoleId)
            select new Row { UserId = u.Id, DisplayName = u.DisplayName, RoleId = (int?)a.RoleId };

        var participants =
            from ea in db.EditAssignments.AsNoTracking()
            join r in db.EditAssignmentRecipients.AsNoTracking() on ea.Id equals r.AssignmentId
            join u in db.Users.AsNoTracking() on r.UserId equals u.Id
            where ea.DocumentId == documentId
                  && u.IsActive
                  && ea.Level == GrantLevel.Approve
                  && (ea.SheetCode == null || sheetCodes.Contains(ea.SheetCode))
                  && (ea.PeriodKeyValue == null || ea.PeriodKeyValue == periodKey)
                  && (ea.ExpiresAt == null || ea.ExpiresAt > now)
            select new Row { UserId = u.Id, DisplayName = u.DisplayName, RoleId = (int?)null };

        var rows = await holders.Union(participants)
            .OrderBy(x => x.UserId).ThenBy(x => x.RoleId)
            .Take(maxCandidates + 1)
            .ToListAsync(ct).ConfigureAwait(false);

        var capped = rows.Count > maxCandidates;

        return new StepApproverFacts(
            steps,
            [.. rows.Take(maxCandidates).Select(x => new StepApproverCandidate(x.UserId, x.DisplayName, x.RoleId))],
            capped);
    }

    private sealed class Row
    {
        public int UserId { get; set; }

        public string DisplayName { get; set; } = string.Empty;

        public int? RoleId { get; set; }
    }
}
