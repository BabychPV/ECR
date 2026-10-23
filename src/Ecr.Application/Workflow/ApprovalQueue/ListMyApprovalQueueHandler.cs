// src/Ecr.Application/Workflow/ApprovalQueue/ListMyApprovalQueueHandler.cs
using System.Globalization;
using Ecr.Application.Documents;
using Ecr.Application.Documents.EditAssignments;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow.ApprovalQueue;

/// <summary>
/// Черга «До погодження» поточного користувача (ФВ-12.5, рішення людини 12.10): подані аркуші, які він
/// МОЖЕ затвердити зараз.
/// </summary>
/// <remarks>
/// ⛔ Рішення «чи його черга» — ТЕ САМЕ, що в <see cref="ApproveSheetHandler"/>: <c>EditRules.CanApprove</c>
/// (видимість проєкту, роль поточного кроку чинного маршруту <c>ApprovalRoute.Resolve</c>, рівень гранта на
/// аркуш у періоді ≥ <c>Approve</c>, стан <c>Submitted</c>, проєкт не архівний) плюс видимість аркуша читачеві
/// (як <c>RequireSheetVisibleAsync</c>). Власне подання в черзі ЛИШАЄТЬСЯ: правило чотирьох очей скасоване
/// (<c>D-440</c>), доступ розподіляють лише ролі.
/// Друга реалізація правил розійшлася б із першою — користувач бачив би в черзі те, що сервер відмовляється
/// затвердити.
///
/// ⚠ Нерозкриття: аркуш, схований від читача, у відповідь не потрапляє взагалі — ні назви, ні факту.
///
/// ⚠ Продуктивність: проєкти, маршрути й знімки шаблонів читаються РАЗ на запит; кандидати — порціями за
/// keyset-курсором, не більше <see cref="MaxScanned"/> за запит (довша черга продовжується за
/// <c>nextCursor</c>). Рішення — в пам'яті, без звернень на кандидата.
/// </remarks>
public sealed class ListMyApprovalQueueHandler(
    IApprovalQueueStore store,
    IAccessDecisionService access,
    IMetadataCache metadata,
    Common.ICurrentUser currentUser)
{
    /// <summary>Розмір сторінки за замовчуванням.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Найбільший розмір сторінки.</summary>
    public const int MaxLimit = 200;

    /// <summary>Розмір порції кандидатів з бази.</summary>
    public const int BatchSize = 200;

    /// <summary>Скільки кандидатів переглядається за один запит.</summary>
    public const int MaxScanned = 1000;

    private static readonly ApprovalQueueDto Empty = new([], null);

    private static readonly IReadOnlyDictionary<long, IReadOnlyList<ParticipationGrant>> NoParticipations =
        new Dictionary<long, IReadOnlyList<ParticipationGrant>>();

    /// <summary>Сторінка черги.</summary>
    /// <param name="limit">Розмір сторінки 1..200; <c>null</c> — 50.</param>
    /// <param name="cursor">Курсор попередньої відповіді; <c>null</c> — з початку.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ApprovalQueueDto> HandleAsync(int? limit, string? cursor, CancellationToken ct)
    {
        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var after = ParseCursor(cursor);

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        // Симуляція «очима користувача» й технічний профіль інтеграції затверджувати не можуть
        // (`EditRules.CanApprove`), тож і черги в них немає.
        if (profile.IsSimulation || profile.IsIntegrationWriter)
        {
            return Empty;
        }

        // ✎ D-444 (AN-131, п.3): учасник документа з наданням — кандидат і поза проєктами з роллю. Надання — з тих
        // самих фактів, що й рішення затвердження (`AccessDecisionService.WithParticipationAsync`); без ознаки
        // профілю — порожньо без бази (бюджет решти користувачів не змінюється).
        var participations = profile.HasDocumentParticipations
            ? await access.ActiveParticipationsAsync(profile, ct).ConfigureAwait(false)
            : NoParticipations;

        var allProjects = (await store.ListProjectsAsync(ct).ConfigureAwait(false)).ToDictionary(p => p.ProjectId);
        var projects = allProjects.Values
            .Where(p => profile.SeesDocumentsOf(p.ProjectId))
            .ToDictionary(p => p.ProjectId);
        if (projects.Count == 0 && participations.Count == 0)
        {
            return Empty;
        }

        var routes = await store.ListActiveRoutesAsync(ct).ConfigureAwait(false);
        var restricted = DocumentSheetVisibility.HasRestrictions(profile);
        var projectIds = projects.Keys.ToList();
        var participatedIds = participations.Keys.ToList();

        var snapshots = new Dictionary<int, TemplateVersionSnapshot>();
        var scopes = new Dictionary<int, DocumentReadScope>();
        var resolved = new Dictionary<int, ApprovalRoute?>();

        var found = new List<(ApprovalQueueItemDto Item, ApprovalQueueCursor Position)>();
        var scanned = 0;
        ApprovalQueueCursor? lastScanned = null;
        var exhausted = false;
        var position = after;

        while (found.Count <= take && scanned < MaxScanned)
        {
            var batch = await store.ListSubmittedAsync(projectIds, restricted, position, BatchSize, participatedIds, ct)
                .ConfigureAwait(false);

            foreach (var row in batch)
            {
                scanned++;
                position = lastScanned = new ApprovalQueueCursor(row.SubmittedAt.Ticks, row.StateId);

                // ⛔ Надання — лише без заборони проєкту (як `CanReadDocumentAsync`/`WithParticipationAsync`); документ
                // проєкту без ролі й без надання (або архівного проєкту) — не кандидат.
                var grants = !profile.DeniesProject(row.ProjectId)
                             && participations.TryGetValue(row.DocumentId, out var mine)
                    ? mine
                    : null;
                if (!allProjects.TryGetValue(row.ProjectId, out var project)
                    || (!projects.ContainsKey(row.ProjectId) && grants is null))
                {
                    continue;
                }

                if (!snapshots.TryGetValue(project.TemplateVersionId, out var snapshot))
                {
                    snapshot = await metadata.GetAsync(project.TemplateVersionId, ct).ConfigureAwait(false);
                    snapshots[project.TemplateVersionId] = snapshot;
                }

                var item = Evaluate(row, project, snapshot, profile, restricted, routes, scopes, resolved, grants);
                if (item is null)
                {
                    continue;
                }

                found.Add((item, lastScanned));
                if (found.Count > take)
                {
                    break;
                }
            }

            if (found.Count > take)
            {
                break;
            }

            if (batch.Count < BatchSize)
            {
                exhausted = true;
                break;
            }
        }

        if (found.Count > take)
        {
            var page = found.Take(take).ToList();
            return new ApprovalQueueDto([.. page.Select(f => f.Item)], FormatCursor(page[^1].Position));
        }

        return new ApprovalQueueDto(
            [.. found.Select(f => f.Item)],
            !exhausted && lastScanned is not null ? FormatCursor(lastScanned) : null);
    }

    private static ApprovalQueueItemDto? Evaluate(
        ApprovalQueueCandidate row,
        ApprovalQueueProject project,
        TemplateVersionSnapshot snapshot,
        AccessProfile profile,
        bool restricted,
        ApprovalQueueRoutes routes,
        Dictionary<int, DocumentReadScope> scopes,
        Dictionary<int, ApprovalRoute?> resolved,
        IReadOnlyList<ParticipationGrant>? grants)
    {
        var sheet = snapshot.Sheets.FirstOrDefault(s => s.Id == row.SheetDefId);
        if (sheet is null)
        {
            return null;
        }

        var key = new PeriodKey(row.PeriodKey);
        PeriodKey? period = key.IsValid ? key : null;

        // Аркуш, схований від читача (Deny/звуження) — для нього не існує: ні в черзі, ні в підрахунку.
        // ✎ D-444: межі документа з наданням — ВЛАСНІ (ті самі, що `ReadScopeAsync` дає затвердженню через
        // `RequireSheetVisibleAsync`), а не межі проєкту за ролями: ділити їх між документами проєкту не можна.
        if (restricted)
        {
            DocumentReadScope? scope;
            if (grants is not null)
            {
                scope = DocumentReadScope.ForParticipant(profile, project.ProjectId, snapshot, grants);
            }
            else if (!scopes.TryGetValue(project.ProjectId, out scope))
            {
                scope = DocumentReadScope.For(profile, project.ProjectId, snapshot);
                scopes[project.ProjectId] = scope;
            }

            var inPeriod = period is { } p ? scope.InPeriod(p) : scope;
            if (!inPeriod.CanReadSheet(row.SheetDefId))
            {
                return null;
            }
        }

        // Маршрут проєкту (єдиний вибір із затвердженням) і крок, на якому стоїть аркуш.
        if (!resolved.TryGetValue(project.ProjectId, out var route))
        {
            route = ApprovalRoute.Resolve(routes.Routes, project.ProjectId, project.TemplateVersionId);
            resolved[project.ProjectId] = route;
        }

        var step = route is { Steps.Count: > 0 } ? route.StepAt(row.CurrentStepId) : null;

        var context = new CellAccessContext(
            project.ProjectId,
            row.SheetDefId,
            TableDefId: 0,
            ColumnDefId: 0,
            project.Status,
            project.IsArchiving,
            PeriodState.Open,
            OutOfAccessWindow: false,
            DocumentStatus.Submitted,
            ColumnIsComputed: false,
            ColumnIsReadOnly: false,
            RowIsReadOnly: false)
        {
            SheetCode = sheet.Code,
            Period = period,

            // ✎ D-444 (AN-131): рівень надання на аркуш — ТА САМА формула, що в `WithParticipationAsync` служби
            // (`DocumentParticipation.LevelAt`, таблиця = null: надання на таблицю аркуша не погоджує). Роль кроку
            // маршруту `CanApprove` перевіряє ДО рівня — надання маршрут не обходить (D-444 п.2).
            ParticipatesInDocument = grants is not null,
            ParticipationLevel = grants is null
                ? GrantLevel.None
                : DocumentParticipation.LevelAt(grants, sheet.Code, null, period),
        };

        if (!EditRules.CanApprove(profile, context, step?.RoleId).IsAllowed)
        {
            return null;
        }

        return new ApprovalQueueItemDto(
            row.DocumentId,
            row.DocumentKey,
            project.ProjectCode,
            project.TemplateCode,
            row.SheetDefId,
            sheet.Code,
            row.PeriodKey,
            row.SubmittedAt,
            row.SubmittedByDisplayName,
            step?.Ordinal,
            route is { Steps.Count: > 0 } ? route.Steps.Count : null,
            step is null ? null : routes.RoleCodes.GetValueOrDefault(step.RoleId));
    }

    private static string FormatCursor(ApprovalQueueCursor cursor)
        => string.Create(CultureInfo.InvariantCulture, $"{cursor.SubmittedAtTicks}.{cursor.StateId}");

    private static ApprovalQueueCursor? ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }

        var parts = cursor.Split('.');
        if (parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            && ticks <= DateTime.MaxValue.Ticks)
        {
            return new ApprovalQueueCursor(ticks, id);
        }

        throw EditAssignmentAccess.Invalid(
            "err.ECR-REQ-0422.approvalQueueCursorInvalid", "Курсор черги погодження недійсний.");
    }
}
