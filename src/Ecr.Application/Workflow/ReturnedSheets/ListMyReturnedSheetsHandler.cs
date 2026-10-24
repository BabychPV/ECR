// src/Ecr.Application/Workflow/ReturnedSheets/ListMyReturnedSheetsHandler.cs
using System.Globalization;
using Ecr.Application.Documents;
using Ecr.Application.Documents.EditAssignments;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow.ReturnedSheets;

/// <summary>
/// ✎ D-449 (рішення людини 24.10: «My tasks» — те, чого чекають від МЕНЕ зараз): аркуші, які погоджувач повернув на
/// доопрацювання і над якими працює поточний користувач.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ «Повернуто» — це стан <c>Rejected</c>, і лише він. Його ставить ЄДИНА дія — відхилення погоджувачем
/// (<c>ApproveSheetHandler</c> з <c>approved=false</c> → <c>ApprovalState.Reject</c>, право <c>EditRules.CanApprove</c>,
/// причина обов'язкова). Відкликання автором (<c>Recall</c>, BE-31) і повернення в роботу з правом
/// <c>Document.Reopen</c> (<c>ReopenDocumentHandler</c>) ведуть у <c>Draft</c> — це не повернення від погоджувача і в
/// блок не потрапляють. Повторне подання виводить аркуш із <c>Rejected</c>, тож задача зникає сама: це живий запит,
/// збережених задач немає.
/// </para>
/// <para>
/// ⛔ Чия задача: (а) власник документа (<c>Document.CreatedByUserId</c>; D-443 — власника можна передати), якщо він
/// бачить проєкт за ролями; (б) учасник із діючим наданням <c>Write</c>/<c>Submit</c>, що відкриває цей аркуш у цьому
/// періоді (<see cref="DocumentParticipation.WorksOnSheet"/> — ті самі факти й збіг області, що й рішення доступу,
/// <see cref="IAccessDecisionService.ActiveParticipationsAsync"/>). Надання <c>Read</c> — лише перегляд, <c>Approve</c> —
/// погоджувач: не їхня задача. Заборона проєкту (<see cref="AccessProfile.DeniesProject"/>) перемагає обидва шляхи.
/// </para>
/// <para>
/// ⚠ Нерозкриття: аркуш, схований від читача, у відповідь не потрапляє (ні назви, ні причини) — межі ті самі, що на
/// сторінці документа (<see cref="DocumentSheetVisibility"/> / <see cref="DocumentReadScope"/>, для учасника — власні
/// межі документа), як і в черзі «До погодження».
/// </para>
/// <para>
/// ⚠ Продуктивність — як <c>ListMyApprovalQueueHandler</c>: проєкти й знімки шаблонів — раз на запит, кандидати —
/// keyset-порціями, не більше <see cref="MaxScanned"/> за запит; рішення в пам'яті, без звернень на кандидата.
/// </para>
/// </remarks>
public sealed class ListMyReturnedSheetsHandler(
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

    private static readonly ReturnedSheetsDto Empty = new([], null);

    private static readonly IReadOnlyDictionary<long, IReadOnlyList<ParticipationGrant>> NoParticipations =
        new Dictionary<long, IReadOnlyList<ParticipationGrant>>();

    /// <summary>Сторінка блоку.</summary>
    /// <param name="limit">Розмір сторінки 1..200; <c>null</c> — 50.</param>
    /// <param name="cursor">Курсор попередньої відповіді; <c>null</c> — з початку.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ReturnedSheetsDto> HandleAsync(int? limit, string? cursor, CancellationToken ct)
    {
        var userId = EditAssignmentAccess.RequireUser(currentUser);
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var after = ParseCursor(cursor);

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        // Симуляція «очима користувача» й технічний профіль інтеграції повторно не подають — задач немає
        // (так само, як порожня черга «До погодження» в них).
        if (profile.IsSimulation || profile.IsIntegrationWriter)
        {
            return Empty;
        }

        var participations = profile.HasDocumentParticipations
            ? await access.ActiveParticipationsAsync(profile, ct).ConfigureAwait(false)
            : NoParticipations;

        var allProjects = (await store.ListProjectsAsync(ct).ConfigureAwait(false)).ToDictionary(p => p.ProjectId);
        var projects = allProjects.Values
            .Where(p => profile.SeesDocumentsOf(p.ProjectId) && !profile.DeniesProject(p.ProjectId))
            .ToDictionary(p => p.ProjectId);
        if (projects.Count == 0 && participations.Count == 0)
        {
            return Empty;
        }

        var restricted = DocumentSheetVisibility.HasRestrictions(profile);
        var projectIds = projects.Keys.ToList();
        var participatedIds = participations.Keys.ToList();

        var snapshots = new Dictionary<int, TemplateVersionSnapshot>();
        var scopes = new Dictionary<int, DocumentReadScope>();

        var found = new List<(ReturnedSheetItemDto Item, ReturnedSheetsCursor Position)>();
        var scanned = 0;
        ReturnedSheetsCursor? lastScanned = null;
        var exhausted = false;
        var position = after;

        while (found.Count <= take && scanned < MaxScanned)
        {
            var batch = await store
                .ListReturnedAsync(userId, projectIds, restricted, position, BatchSize, participatedIds, ct)
                .ConfigureAwait(false);

            foreach (var row in batch)
            {
                scanned++;
                position = lastScanned = new ReturnedSheetsCursor(row.ReturnedAt.Ticks, row.StateId);

                // ⛔ Заборона проєкту перемагає і власника, і надання; архівний проєкт — не кандидат.
                if (profile.DeniesProject(row.ProjectId) || !allProjects.TryGetValue(row.ProjectId, out var project))
                {
                    continue;
                }

                var owner = row.OwnerUserId == userId && projects.ContainsKey(row.ProjectId);
                var grants = participations.TryGetValue(row.DocumentId, out var mine) ? mine : null;
                if (!owner && grants is null)
                {
                    continue;
                }

                if (!snapshots.TryGetValue(project.TemplateVersionId, out var snapshot))
                {
                    snapshot = await metadata.GetAsync(project.TemplateVersionId, ct).ConfigureAwait(false);
                    snapshots[project.TemplateVersionId] = snapshot;
                }

                var item = Evaluate(row, project, snapshot, profile, restricted, scopes, owner, grants);
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
            return new ReturnedSheetsDto([.. page.Select(f => f.Item)], FormatCursor(page[^1].Position));
        }

        return new ReturnedSheetsDto(
            [.. found.Select(f => f.Item)],
            !exhausted && lastScanned is not null ? FormatCursor(lastScanned) : null);
    }

    private static ReturnedSheetItemDto? Evaluate(
        ReturnedSheetCandidate row,
        ApprovalQueueProject project,
        TemplateVersionSnapshot snapshot,
        AccessProfile profile,
        bool restricted,
        Dictionary<int, DocumentReadScope> scopes,
        bool owner,
        IReadOnlyList<ParticipationGrant>? grants)
    {
        var sheet = snapshot.Sheets.FirstOrDefault(s => s.Id == row.SheetDefId);
        if (sheet is null)
        {
            return null;
        }

        var key = new PeriodKey(row.PeriodKey);
        PeriodKey? period = key.IsValid ? key : null;

        // Учасник (не власник) — лише якщо його надання Write/Submit відкриває саме цей аркуш у цьому періоді.
        if (!owner && (grants is null || !DocumentParticipation.WorksOnSheet(grants, sheet.Code, period)))
        {
            return null;
        }

        // ⛔ Аркуш, схований від читача, для нього не існує. Межі документа з наданням — власні (ForParticipant), не
        // межі проєкту за ролями: ділити їх між документами проєкту не можна (як у черзі «До погодження»).
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

        return new ReturnedSheetItemDto(
            row.DocumentId,
            row.DocumentKey,
            row.DocumentNameL10n,
            project.ProjectCode,
            row.SheetDefId,
            sheet.Code,
            sheet.NameL10n,
            row.PeriodKey,
            row.ReturnedAt,
            row.ReturnedByDisplayName,
            row.Reason);
    }

    private static string FormatCursor(ReturnedSheetsCursor cursor)
        => string.Create(CultureInfo.InvariantCulture, $"{cursor.ReturnedAtTicks}.{cursor.StateId}");

    private static ReturnedSheetsCursor? ParseCursor(string? cursor)
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
            return new ReturnedSheetsCursor(ticks, id);
        }

        throw EditAssignmentAccess.Invalid(
            "err.ECR-REQ-0422.returnedSheetsCursorInvalid", "Курсор блоку «Повернуто на доопрацювання» недійсний.");
    }
}
