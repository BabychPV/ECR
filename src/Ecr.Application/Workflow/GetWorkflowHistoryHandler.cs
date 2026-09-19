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
public sealed record WorkflowEventDto(
    string SheetCode,
    string FromState,
    string ToState,
    string Action,
    string ByDisplayName,
    DateTime At,
    string? Reason,
    int? StepOrdinal);

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
public sealed class GetWorkflowHistoryHandler(GetDocumentHandler getDocument, IWorkflowStore workflow)
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
    public async Task<IReadOnlyList<WorkflowEventDto>> HandleAsync(
        long documentId, int periodKey, CancellationToken ct)
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

        var events = await workflow.GetHistoryAsync(documentId, key, MaxEvents, ct).ConfigureAwait(false);

        return events
            .Select(e => new WorkflowEventDto(
                e.SheetCode,
                e.FromStatus.ToString(),
                e.ToStatus.ToString(),
                e.Action.ToString(),
                ActorName(e),
                e.At,
                e.Reason,
                e.StepOrdinal))
            .ToList();
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
