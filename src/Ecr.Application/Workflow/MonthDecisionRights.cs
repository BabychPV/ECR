// src/Ecr.Application/Workflow/MonthDecisionRights.cs
using Ecr.Application.Documents;
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
/// <item><c>CanDecide</c> (місяць <c>Submitted</c>) — <see cref="ApproverEditRule"/> (<c>CanApproveOfAsync</c>), як
/// <c>POST …/approve</c> і <c>GET …/approver-edit</c>;</item>
/// <item><c>CanRecall</c> (місяць <c>Submitted</c>) — правило <see cref="RecallSheetHandler.CanRecallAsync"/>: автор
/// подання, жоден крок не підписано (<see cref="ApprovalState.IsRecallableState"/>), рівень
/// подання (<c>HasSubmitGrantAsync</c>);</item>
/// <item><c>CanReturn</c> (місяць <c>Approved</c>) — право <c>Document.Reopen</c> і <c>CanReopenOfAsync</c>, як
/// <c>POST …/reopen</c>. Повернення ПОДАНОГО місяця в річній розкладці — це «Reject» (свідома відмінність від звичайного
/// документа, D-559).</item>
/// </list>
/// <para>
/// ⚠ Ціна: рішення рахуються лише для місяців у стані, де дія взагалі можлива (≤ 12), автор подання й крок — з календаря
/// (одне читання станів), перший крок маршруту й право <c>Document.Reopen</c> — раз на запит; запит гранта відкликання —
/// лише для власних подань на першому кроці. Чернетки не коштують нічого.
/// </para>
/// </remarks>
public static class MonthDecisionRights
{
    /// <summary>Місячні колонки з правами поточного користувача.</summary>
    /// <param name="columns">Колонки зі станом свого місяця.</param>
    /// <param name="calendar">Календар подання аркуша (стан, автор подання, крок — на кожен період).</param>
    /// <param name="access">Служба рішень доступу.</param>
    /// <param name="workflow">Сховище процесу (маршрут).</param>
    /// <param name="documents">Сховище документів (проєкт, версія шаблону).</param>
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

        var decide = new HashSet<int>();
        foreach (var month in submitted)
        {
            if (await ApproverEditRule
                    .IsCurrentStepApproverAsync(access, profile, documentId, sheetDefId, new PeriodKey(month), ct, sheetCopyId)
                    .ConfigureAwait(false))
            {
                decide.Add(month);
            }
        }

        var recall = await RecallableAsync(
                submitted, byKey, access, workflow, documents, profile, userId, documentId, sheetDefId, sheetCopyId, ct)
            .ConfigureAwait(false);

        var giveBack = new HashSet<int>();
        if (approved.Count > 0 && await ReopenDocumentHandler.HasPermissionAsync(access, profile, documentId, ct).ConfigureAwait(false))
        {
            foreach (var month in approved)
            {
                var decision = await access
                    .CanReopenOfAsync(profile, documentId, sheetDefId, new PeriodKey(month), sheetCopyId, ct)
                    .ConfigureAwait(false);
                if (decision.IsAllowed)
                {
                    giveBack.Add(month);
                }
            }
        }

        return
        [
            .. columns.Select(c => c with
            {
                CanDecide = decide.Contains(c.PeriodKey),
                CanRecall = recall.Contains(c.PeriodKey),
                CanReturn = giveBack.Contains(c.PeriodKey),
            }),
        ];
    }

    /// <summary>Подані місяці, які користувач може відкликати (правило <see cref="RecallSheetHandler.CanRecallAsync"/>).</summary>
    private static async Task<HashSet<int>> RecallableAsync(
        IReadOnlyList<int> submitted,
        Dictionary<int, SubmissionCalendarPeriod> byKey,
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
        var result = new HashSet<int>();

        // Лише власні подання — решта не коштує жодного запиту.
        var own = submitted.Where(k => byKey[k].SubmittedByUserId == userId).ToList();
        if (own.Count == 0)
        {
            return result;
        }

        var projectId = await documents.FindProjectIdAsync(documentId, ct).ConfigureAwait(false);
        if (projectId is null)
        {
            return result;
        }

        var firstStepId = await RecallSheetHandler
            .FirstStepIdAsync(workflow, documents, documentId, projectId, ct)
            .ConfigureAwait(false);

        foreach (var month in own)
        {
            var period = byKey[month];
            if (!ApprovalState.IsRecallableState(period.SheetStatus, period.CurrentStepId, firstStepId))
            {
                continue;
            }

            if (await access
                    .HasSubmitGrantAsync(profile, documentId, sheetDefId, new PeriodKey(month), sheetCopyId, ct)
                    .ConfigureAwait(false))
            {
                result.Add(month);
            }
        }

        return result;
    }
}
