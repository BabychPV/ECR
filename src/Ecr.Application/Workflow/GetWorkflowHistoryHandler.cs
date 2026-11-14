// src/Ecr.Application/Workflow/GetWorkflowHistoryHandler.cs
using System.Globalization;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>Подія журналу переходів стану аркуша (<c>BE-11b</c>).</summary>
/// <param name="SheetCode">Код аркуша.</param>
/// <param name="FromState">Стан до дії (<c>DocumentStatus</c>).</param>
/// <param name="ToState">Стан після дії.</param>
/// <param name="Action">Дія (<c>ApprovalAction</c>).</param>
/// <param name="ByDisplayName">Відображуване ім'я виконавця; <c>system</c> — системний перехід.</param>
/// <param name="At">Момент дії, UTC.</param>
/// <param name="Reason">Причина відхилення або повернення в роботу.</param>
/// <param name="StepOrdinal">Крок маршруту, якщо маршрут є.</param>
/// <param name="ApprovedBySubmitter">Погодження виконав той самий користувач, що й подання перед ним: позначка «погоджено автором подання» (<c>D-440</c>).</param>
/// <param name="SheetCopyId">RS: копія аркуша, якої стосується подія; <c>null</c> — оригінал (код аркуша в копії той самий).</param>
/// <param name="PeriodKey">
/// ✎ LAND-YEAR L6: лише <c>year=true</c> — період (місяць року) події; <c>null</c> — відповідь одного періоду.
/// </param>
public sealed record WorkflowEventDto(
    string SheetCode,
    string FromState,
    string ToState,
    string Action,
    string ByDisplayName,
    DateTime At,
    string? Reason,
    int? StepOrdinal,
    bool ApprovedBySubmitter,
    long? SheetCopyId = null,
    int? PeriodKey = null);

/// <summary>
/// Журнал переходів документа за період. Право <c>Document.View</c>.
/// </summary>
/// <remarks>
/// ⛔ Доступ вирішує САМ <see cref="GetDocumentHandler"/>, а не копія його
/// умови: історія погоджень — факт про документ, і відповідь на «чужий» та
/// «неіснуючий» мусить збігатися з <c>GET /documents/{id}</c> (те саме
/// <c>404</c>), інакше за різницею видно, які документи існують.
///
/// ⚠ Таблиця <c>wf.ApprovalEvent</c> починається порожньою: порожній перелік
/// для документа, старшого за міграцію, — не помилка.
/// </remarks>
public sealed class GetWorkflowHistoryHandler(
    GetDocumentHandler getDocument,
    IWorkflowStore workflow,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser,
    IMonthColumnRouter? monthRouter = null)
{
    /// <summary>Право читання журналу — те саме, що й читання документа.</summary>
    /// <remarks>
    /// ⚠ Перевіряє його <see cref="GetDocumentHandler"/> (тест
    /// <c>Без_права_Document_View_відмова_403</c>); тут воно НАЗВАНЕ, бо статичний
    /// сторож <c>Кожен_ендпоінт_перевіряє_саме_своє_право_СТАТИЧНО</c> читає тіло
    /// класу й за впровадженим обробником не ходить.
    /// </remarks>
    public const string Permission = ListDocumentsHandler.Permission;

    /// <summary>Стеля відповіді: журнал читають очима, не вивантажують.</summary>
    public const int MaxEvents = 200;

    /// <summary>Підпис виконавця для системного переходу (<c>ByUserId = null</c>).</summary>
    public const string SystemActor = "system";

    /// <summary>Повертає події документа за період, найновіші перші.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="sheetCopyId">Копія аркуша (RS); <c>null</c> — лише події оригіналів, НЕ всі.</param>
    /// <param name="year">
    /// ✎ LAND-YEAR L6: документ річної розкладки — події ВСІХ місяців року періоду разом (подання й погодження йдуть по
    /// місяцях, а документ один), найновіші перші, кожна з періодом; межі читання — свого місяця (D-214). Стеля та сама
    /// (<see cref="MaxEvents"/>). Інша розкладка — як без прапора.
    /// </param>
    public async Task<IReadOnlyList<WorkflowEventDto>> HandleAsync(
        long documentId, int periodKey, CancellationToken ct, long? sheetCopyId = null, bool year = false)
    {
        var key = PeriodKey.Parse(periodKey);

        var document = await getDocument.HandleAsync(documentId, periodKey, ct).ConfigureAwait(false);
        if (document is null)
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Документ {documentId} не знайдено.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["messageKey"] = "err.ECR-DOC-0404.document",
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var months = year
            ? await YearDocumentPeriods.ResolveAsync(monthRouter, documentId, key, ct).ConfigureAwait(false)
            : null;
        if (months is not null)
        {
            return await YearAsync(documentId, document.ProjectId, months, sheetCopyId, ct).ConfigureAwait(false);
        }

        var events = await workflow.GetHistoryAsync(documentId, key, MaxEvents, ct, sheetCopyId).ConfigureAwait(false);

        // ⛔ L1-18 (D-214, ФВ-6.6): подія називає аркуш, виконавця й текст причини повернення чи
        // відмови — це дані аркуша; закритого користувачу аркуша подій немає. Межі — для
        // періоду запиту. Без подій межі не потрібні.
        if (events.Count > 0)
        {
            var profile = await Security.PermissionCheck
                .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
                .ConfigureAwait(false);
            await Documents.DocumentVisibility
                .RequireInDocumentAsync(access, profile, documentId, document.ProjectId, Permission, ct).ConfigureAwait(false);
            var readable = (await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false)).InPeriod(key);
            events = [.. events.Where(e => readable.CanReadSheetCode(e.SheetCode))];
        }

        return events
            .Select(e => new WorkflowEventDto(
                e.SheetCode,
                e.FromStatus.ToString(),
                e.ToStatus.ToString(),
                e.Action.ToString(),
                ActorName(e),
                e.At,
                e.Reason,
                e.StepOrdinal,
                e.ApprovedBySubmitter,
                e.SheetCopyId))
            .ToList();
    }

    /// <summary>
    /// ✎ LAND-YEAR L6: події всіх місяців року — кожен місяць своїм читанням і СВОЇМИ межами читання (D-214: роль,
    /// обмежена періодами, не бачить подій поза ними), разом за часом, не більше <see cref="MaxEvents"/>.
    /// </summary>
    private async Task<IReadOnlyList<WorkflowEventDto>> YearAsync(
        long documentId, int projectId, IReadOnlyList<int> months, long? sheetCopyId, CancellationToken ct)
    {
        var all = new List<(int Period, ApprovalEventRecord Event)>();
        foreach (var month in months)
        {
            var events = await workflow.GetHistoryAsync(documentId, new PeriodKey(month), MaxEvents, ct, sheetCopyId)
                .ConfigureAwait(false);
            all.AddRange(events.Select(e => (month, e)));
        }

        if (all.Count == 0)
        {
            return [];
        }

        var profile = await Security.PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);
        await Documents.DocumentVisibility
            .RequireInDocumentAsync(access, profile, documentId, projectId, Permission, ct).ConfigureAwait(false);
        var anyPeriod = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        var scopes = new Dictionary<int, Security.DocumentReadScope>();

        return [.. all
            .Where(x =>
            {
                if (!scopes.TryGetValue(x.Period, out var readable))
                {
                    scopes[x.Period] = readable = anyPeriod.InPeriod(new PeriodKey(x.Period));
                }

                return readable.CanReadSheetCode(x.Event.SheetCode);
            })
            .OrderByDescending(x => x.Event.At)
            .ThenByDescending(x => x.Event.Id)
            .Take(MaxEvents)
            .Select(x => new WorkflowEventDto(
                x.Event.SheetCode,
                x.Event.FromStatus.ToString(),
                x.Event.ToStatus.ToString(),
                x.Event.Action.ToString(),
                ActorName(x.Event),
                x.Event.At,
                x.Event.Reason,
                x.Event.StepOrdinal,
                x.Event.ApprovedBySubmitter,
                x.Event.SheetCopyId,
                x.Period))];
    }

    /// <remarks>
    /// ⚠ Користувача з таким ідентифікатором може вже не бути (зовнішнього ключа
    /// на <c>ByUserId</c> немає). Тоді показуємо <c>#id</c>, а НЕ <c>system</c>:
    /// дію виконала людина, і приписати її системі означало б збрехати в журналі.
    /// </remarks>
    private static string ActorName(ApprovalEventRecord e)
        => e.ByUserId is null
            ? SystemActor
            : e.ByDisplayName ?? "#" + e.ByUserId.Value.ToString(CultureInfo.InvariantCulture);
}
