using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>
/// Повернення поданого аркуша в <c>Draft</c> для правки (ФВ-5.20a, D-67).
/// </summary>
/// <remarks>
/// Повторне подання створює **новий** зріз; старий лишається `Submitted`
/// назавжди і не перераховується ніколи (ФВ-9.17). Саме тому правка поданої
/// форми — окрема дія з причиною, а не просто редагування.
/// </remarks>
public sealed class ReopenDocumentHandler(
    IWorkflowStore workflow,
    IAccessDecisionService access,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право, без якого повернення в роботу неможливе (ФВ-6.12).</summary>
    public const string Permission = "Document.Reopen";

    /// <summary>Повертає аркуш у <c>Draft</c>.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="reason">Причина; обов'язкова.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">Період закритий — <c>ECR-PRD-4223</c>.</exception>
    public async Task HandleAsync(
        long documentId, int sheetDefId, int periodKey, string reason, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не може відкривати аркуші.");

        var key = new PeriodKey(periodKey);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        // Право небезпечне і тому перевіряється окремо від грантів: воно дає
        // змогу змінити вже подані числа (ФВ-6.12).
        if (!profile.Has(Permission))
        {
            throw new AccessDeniedException(
                "ECR-ACCS-0403", $"Потрібне право {Permission}.");
        }

        // ⛔ Q-173 (аудит фази 2, авторизація). Глобальне право вище каже «ця
        // людина взагалі може повертати аркуші в роботу»; рішення тут каже
        // «саме на ЦЕЙ документ». Submit і Approve того самого документа
        // перевіряють обидва рівні (CanSubmitAsync/CanApproveAsync) — Reopen,
        // що скасовує будь-яке з них, не мав перевіряти менше.
        var reopenDecision = await access.CanReopenAsync(profile, documentId, sheetDefId, key, ct)
                                          .ConfigureAwait(false);
        if (!reopenDecision.IsAllowed)
        {
            throw new AccessDeniedException(
                "ECR-ACCS-0403",
                $"Повернення аркуша {sheetDefId} у роботу відхилено: {reopenDecision.Reason}.",
                new Dictionary<string, object?> { ["reason"] = reopenDecision.Reason.ToString() });
        }

        // ⛔ Блокування періоду, перевірка стану і запис — В ОДНІЙ транзакції
        // (аудит 2026-09-16, §6.1). До цього фіксу транзакції не було зовсім, і
        // `UPDLOCK` не давав НІЧОГО: поза явною транзакцією він звільняється
        // щойно завершується сам `SELECT` — задовго до `Reopen` і до
        // `SaveChangesAsync`. Коментар обіцяв взаємовиключення з
        // `PeriodStateJob`, а блокування не трималося.
        //
        // Сценарій: `PeriodStateJob` читає період і вирішує закрити його; до
        // його коміту Reopen бачить ще Open, дозволяє і комітить; задача потім
        // комітить перехід у Closed, тихо перекриваючи Reopen — конфлікту
        // немає, бо в `Period` немає RowVersion.
        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⚠ Період береться з UPDLOCK ДО будь-яких змін (ФВ-1.10a) — тепер
            // блокування справді тримається до кінця транзакції.
            var period = await workflow.LockPeriodAsync(documentId, key, innerCt).ConfigureAwait(false);
            if (period.State == PeriodState.Closed)
            {
                throw new BusinessRuleException(
                    "ECR-PRD-4223",
                    $"Період {periodKey} закрито: спершу відкрийте період, потім аркуш.",
                    new Dictionary<string, object?> { ["periodState"] = period.State.ToString() });
            }

            var state = await workflow
                .GetOrCreateAsync(documentId, sheetDefId, key, innerCt)
                .ConfigureAwait(false);
            var fromStatus = state.Status;
            var now = clock.UtcNow;
            state.Reopen(userId, reason, now);

            // `BE-11`: перехід і його причина — у журнал тим самим комітом.
            await workflow.AddEventAsync(
                ApprovalEvent.For(state, fromStatus, ApprovalAction.Reopen, userId, now, reason),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
