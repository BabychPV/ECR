// src/Ecr.Application/Workflow/ReturnedSheets/ListMyReturnedSheetsHandler.cs
using System.Globalization;
using Ecr.Application.Documents;
using Ecr.Application.Documents.EditAssignments;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow.ReturnedSheets;

/// <summary>
/// ✎ D-449 (рішення людини 24.10: «My tasks» — те, чого чекають від МЕНЕ зараз): аркуші, повернені на доопрацювання,
/// над якими працює поточний користувач.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ «Повернуто» (рішення координатора, узгоджено з поштовими подіями lane C): (а) стан <c>Rejected</c> — його ставить
/// лише відхилення погоджувачем (<c>ApproveSheetHandler</c> з <c>approved=false</c>, причина обов'язкова); (б) стан
/// <c>Draft</c>, чия ОСТАННЯ подія журналу аркуша й періоду — <c>Reopen</c> (повернення в роботу правом
/// <c>Document.Reopen</c>, причина обов'язкова), якщо повернув НЕ сам одержувач задачі. Відкликання автором
/// (<c>Recall</c>, BE-31) теж веде в <c>Draft</c>, але останньою подією стає <c>Recall</c> — не задача. Повторне подання
/// виводить аркуш із обох станів, тож задача зникає сама: це живий запит, збережених задач немає.
/// </para>
/// <para>
/// ⛔ Чия задача (одна картка на аркуш × період, хоч би якими шляхами людина до неї дійшла):
/// (1) власник документа (<c>Document.CreatedByUserId</c>; D-443 — власника можна передати), якщо бачить проєкт за ролями;
/// (2) учасник із діючим наданням рівня ≥ <c>Write</c>, що відкриває цей аркуш у періоді
/// (<see cref="DocumentParticipation.WorksOnSheet"/>, факти — <see cref="IAccessDecisionService.ActiveParticipationsAsync"/>);
/// (3) останній подавач аркуша за період (<c>ApprovalState.SubmittedByUserId</c> — його ставить лише подання, тож це
/// той самий користувач, що в останній події <c>Submit</c> журналу), якщо досі має право писати в аркуш: ефективний
/// рівень ролей <see cref="EditRules.Effective"/> ≥ <c>Write</c> (той самий розрахунок, що в рішенні про запис; він же
/// вимагає видимого проєкту) або надання учасника з (2). Нового правила немає.
/// Заборона проєкту (<see cref="AccessProfile.DeniesProject"/>) перемагає всі шляхи.
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

                // Повернув сам — не задача (повернення в роботу власною рукою людина й так знає).
                if (row.Kind == ReturnKind.Reopened && row.ReturnedByUserId == userId)
                {
                    continue;
                }

                var owner = row.OwnerUserId == userId && projects.ContainsKey(row.ProjectId);
                var grants = participations.TryGetValue(row.DocumentId, out var mine) ? mine : null;
                var submitter = row.SubmittedByUserId == userId;
                if (!owner && grants is null && !submitter)
                {
                    continue;
                }

                if (!snapshots.TryGetValue(project.TemplateVersionId, out var snapshot))
                {
                    snapshot = await metadata.GetAsync(project.TemplateVersionId, ct).ConfigureAwait(false);
                    snapshots[project.TemplateVersionId] = snapshot;
                }

                var item = Evaluate(row, project, snapshot, profile, restricted, scopes, owner, submitter, grants);
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
        bool submitter,
        IReadOnlyList<ParticipationGrant>? grants)
    {
        var sheet = snapshot.Sheets.FirstOrDefault(s => s.Id == row.SheetDefId);
        if (sheet is null)
        {
            return null;
        }

        var key = new PeriodKey(row.PeriodKey);
        PeriodKey? period = key.IsValid ? key : null;

        // Учасник — лише з наданням ≥ Write, що відкриває саме цей аркуш у цьому періоді; останній подавач — лише з
        // правом писати в аркуш (ролі або те саме надання). Власник проходить і так.
        var participantWrites = grants is not null && DocumentParticipation.WorksOnSheet(grants, sheet.Code, period);
        if (!owner
            && !participantWrites
            && !(submitter && RolesWriteSheet(profile, project, sheet.Id, sheet.Code, period, grants)))
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

    /// <summary>
    /// Чи дає роль право писати в аркуш у періоді — ефективний рівень <see cref="EditRules.Effective"/> (той самий, що в
    /// рішенні про запис комірки) ≥ <c>Write</c>; заборони й невидимий проєкт дають <c>None</c>.
    /// </summary>
    private static bool RolesWriteSheet(
        AccessProfile profile,
        ApprovalQueueProject project,
        int sheetDefId,
        string sheetCode,
        PeriodKey? period,
        IReadOnlyList<ParticipationGrant>? grants)
    {
        var context = new CellAccessContext(
            project.ProjectId,
            sheetDefId,
            TableDefId: 0,
            ColumnDefId: 0,
            project.Status,
            project.IsArchiving,
            PeriodState.Open,
            OutOfAccessWindow: false,
            DocumentStatus.Draft,
            ColumnIsComputed: false,
            ColumnIsReadOnly: false,
            RowIsReadOnly: false)
        {
            SheetCode = sheetCode,
            Period = period,
            ParticipatesInDocument = grants is not null,
            ParticipationLevel = grants is null ? GrantLevel.None : DocumentParticipation.LevelAt(grants, sheetCode, null, period),
        };

        return EditRules.Effective(profile, context) >= GrantLevel.Write;
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
