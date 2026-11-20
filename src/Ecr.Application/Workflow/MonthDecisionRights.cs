// src/Ecr.Application/Workflow/MonthDecisionRights.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// ✎ D-6: права поточного користувача над КОЖНИМ місяцем річного аркуша (розкладка «рік») — для місячних колонок
/// відповіді <c>GET …/submission-target</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ У річному аркуші стан погодження свій у кожного місяця, і право теж: квітень може стояти на кроці 2 маршруту (не
/// мій), а травень — на кроці 1 (мій); відкликати можна лише власне подання, доки перший крок не підписано. Клієнт, що
/// питав право лише для одного (обраного) місяця, показував ціль, над якою людина нічого не може, і не давав дійти до
/// місяця, над яким може. Тепер право кожного місяця рахує сервер — ТИМ САМИМ правилом, що й дія:
/// </para>
/// <list type="bullet">
/// <item><c>CanDecide</c> (місяць <c>Submitted</c>) — <c>CanApproveOfAsync</c>, як <c>POST …/approve</c> і
/// <c>GET …/approver-edit</c>; версія шаблону місяця без погодження (<see cref="WorkflowModeGuard"/>) — ні;</item>
/// <item><c>CanRecall</c> (місяць <c>Submitted</c>) — правило <see cref="RecallSheetHandler"/>: автор подання, жоден крок
/// не підписано (<see cref="ApprovalState.IsRecallableState"/>), рівень подання (<c>HasSubmitGrantAsync</c>), версія з
/// погодженням (<see cref="WorkflowModeGuard"/>), і, як <c>ClosedPeriodGuard</c> дії, — місяць не закрито (ефективний
/// стан календаря з винятком «відкрито для документа»), проєкт не архівний і не архівується;</item>
/// <item><c>CanReturn</c> (місяць <c>Approved</c>) — право <c>Document.Reopen</c> і <c>CanReopenOfAsync</c>, як
/// <c>POST …/reopen</c>, і та сама відмова <c>ClosedPeriodGuard</c> (закритий місяць, архівний проєкт). Повернення
/// ПОДАНОГО місяця в річній розкладці — це «Reject» (свідома відмінність від звичайного документа, D-559).</item>
/// </list>
/// <para>
/// ⚠ Ціна не залежить від кількості місяців: стан, автор подання й крок — з календаря (одне читання станів); режим
/// погодження місяців — одним запитом; рішення доступу всіх місяців — одним пакетом
/// (<see cref="IAccessDecisionService.SheetPeriodRightsAsync"/>: умови двома запитами, маршрут раз на версію шаблону);
/// перший крок маршруту (відкликання) і право <c>Document.Reopen</c> — раз на запит і лише коли є що питати. Чернетки не
/// коштують нічого.
/// </para>
/// </remarks>
public static class MonthDecisionRights
{
    /// <summary>Місячні колонки з правами поточного користувача.</summary>
    /// <param name="columns">Колонки зі станом свого місяця.</param>
    /// <param name="calendar">Календар подання аркуша (ефективний стан, стан аркуша, автор подання, крок — на кожен період).</param>
    /// <param name="access">Служба рішень доступу.</param>
    /// <param name="workflow">Сховище процесу (маршрут).</param>
    /// <param name="documents">Сховище документів (проєкт, версія шаблону, режим погодження).</param>
    /// <param name="profile">Профіль прав користувача.</param>
    /// <param name="userId">Користувач.</param>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="sheetCopyId">Копія аркуша (RS); <c>null</c> — оригінал.</param>
    /// <param name="ct">Токен скасування.</param>
    public static async Task<IReadOnlyList<MonthColumnStatusDto>> ApplyAsync(
        IReadOnlyList<MonthColumnStatusDto> columns,
        SubmissionCalendar calendar,
        IAccessDecisionService access,
        IWorkflowStore workflow,
        IDocumentStore documents,
        AccessProfile profile,
        int userId,
        long documentId,
        int sheetDefId,
        long? sheetCopyId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(documents);

        var byKey = calendar.Periods.ToDictionary(p => p.PeriodKey);
        var monthKeys = columns.Select(c => c.PeriodKey).Distinct().Order().ToList();

        var submitted = monthKeys
            .Where(k => byKey.TryGetValue(k, out var p) && p.SheetStatus == DocumentStatus.Submitted)
            .ToList();
        var approved = monthKeys
            .Where(k => byKey.TryGetValue(k, out var p) && p.SheetStatus == DocumentStatus.Approved)
            .ToList();
        if (submitted.Count == 0 && approved.Count == 0)
        {
            return columns;
        }

        // ⛔ L1 (B75): версія шаблону місяця без погодження — затвердження й відкликання сервер відхиляє (422 workflowDisabled).
        var workflowNone = submitted.Count == 0
            ? new HashSet<int>()
            : await documents.GetWorkflowNonePeriodsAsync(documentId, submitted, ct).ConfigureAwait(false);
        var decideKeys = submitted.Where(k => !workflowNone.Contains(k)).ToList();

        // ⛔ M1: повернути в Draft (Recall, Reopen) не можна в закритому місяці й в архівному чи архівованому проєкті —
        // `ClosedPeriodGuard` дії; стан — ефективний із календаря (з винятком «місяць відкрито для документа»).
        var projectLocked = calendar.ProjectStatus == ProjectStatus.Archived || calendar.IsArchiving;
        bool Reversible(int key) => !projectLocked && byKey[key].State != PeriodState.Closed;

        var recallCandidates = await RecallCandidatesAsync(
                decideKeys.Where(Reversible).ToList(), byKey, workflow, documents, userId, documentId, ct)
            .ConfigureAwait(false);

        var returnCandidates = approved.Where(Reversible).ToList();
        if (returnCandidates.Count > 0
            && !await ReopenDocumentHandler.HasPermissionAsync(access, profile, documentId, ct).ConfigureAwait(false))
        {
            returnCandidates = [];
        }

        if (decideKeys.Count == 0 && recallCandidates.Count == 0 && returnCandidates.Count == 0)
        {
            return columns;
        }

        var rights = await access
            .SheetPeriodRightsAsync(
                profile,
                documentId,
                sheetDefId,
                sheetCopyId,
                [.. decideKeys.Select(k => new PeriodKey(k))],
                [.. recallCandidates.Select(k => new PeriodKey(k))],
                [.. returnCandidates.Select(k => new PeriodKey(k))],
                ct)
            .ConfigureAwait(false);

        return
        [
            .. columns.Select(c => c with
            {
                CanDecide = rights.Approve.Contains(c.PeriodKey),
                CanRecall = recallCandidates.Contains(c.PeriodKey) && rights.SubmitGrant.Contains(c.PeriodKey),
                CanReturn = returnCandidates.Contains(c.PeriodKey) && rights.Reopen.Contains(c.PeriodKey),
            }),
        ];
    }

    /// <summary>
    /// Подані місяці, де відкликання не відмовить ще до гранта: власне подання на першому кроці маршруту (правило
    /// <see cref="RecallSheetHandler.CanRecallAsync"/>); грант подання питає пакет.
    /// </summary>
    private static async Task<List<int>> RecallCandidatesAsync(
        IReadOnlyList<int> submitted,
        Dictionary<int, SubmissionCalendarPeriod> byKey,
        IWorkflowStore workflow,
        IDocumentStore documents,
        int userId,
        long documentId,
        CancellationToken ct)
    {
        // Лише власні подання — решта не коштує жодного запиту.
        var own = submitted.Where(k => byKey[k].SubmittedByUserId == userId).ToList();
        if (own.Count == 0)
        {
            return [];
        }

        var projectId = await documents.FindProjectIdAsync(documentId, ct).ConfigureAwait(false);
        if (projectId is null)
        {
            return [];
        }

        var firstStepId = await RecallSheetHandler
            .FirstStepIdAsync(workflow, documents, documentId, projectId, ct)
            .ConfigureAwait(false);

        return [.. own.Where(k => ApprovalState.IsRecallableState(byKey[k].SheetStatus, byKey[k].CurrentStepId, firstStepId))];
    }
}
