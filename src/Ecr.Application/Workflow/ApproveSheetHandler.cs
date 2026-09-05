using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>Затвердження або відхилення аркуша за маршрутом (ФВ-5.13…ФВ-5.17).</summary>
public sealed class ApproveSheetHandler(
    IWorkflowStore workflow,
    IAccessDecisionService access,
    IUnitOfWork uow,
    INotificationOutbox outbox,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Затверджує або відхиляє аркуш.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="approved"><c>true</c> — затвердити, <c>false</c> — відхилити.</param>
    /// <param name="reason">Коментар; обов'язковий при відхиленні (ФВ-5.15).</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(
        long documentId, int sheetDefId, int periodKey, bool approved, string? reason, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не може затверджувати.");

        var key = new PeriodKey(periodKey);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var decision = await access.CanApproveAsync(profile, documentId, sheetDefId, key, ct)
                                   .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            throw new AccessDeniedException(
                "ECR-ACCS-0403",
                $"Затвердження аркуша {sheetDefId} відхилено: {decision.Reason}.",
                new Dictionary<string, object?> { ["reason"] = decision.Reason.ToString() });
        }

        var state = await workflow.GetOrCreateAsync(documentId, sheetDefId, key, ct).ConfigureAwait(false);
        var now = clock.UtcNow;

        if (approved)
        {
            state.Approve(userId, now);
        }
        else
        {
            // Домен вимагає коментаря сам; тут лише переклад порожнього рядка
            // в null, щоб повідомлення було про суть, а не про пробіли.
            state.Reject(userId, reason ?? string.Empty, now);
        }

        // ⛔ Статус ДОКУМЕНТА не чіпається: його немає (D-93). Зведений стан
        // рахується запитом по wf.ApprovalState, і скалярне поле рано чи пізно
        // показало б Approved там, де половина аркушів у Draft.

        // Друга подія черги: затвердження аркуша (`A7-31`).
        await outbox.EnqueueAsync(
            "sheet.approved",
            $"Аркуш {sheetDefId} документа {documentId} затверджено",
            "Аркуш затверджено; подальші зміни потребують повернення в роботу.",
            recipients: null,
            ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
