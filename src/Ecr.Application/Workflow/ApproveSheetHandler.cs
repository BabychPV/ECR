using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>Затвердження або відхилення аркуша за маршрутом (ФВ-5.13…ФВ-5.17).</summary>
public sealed class ApproveSheetHandler(
    IWorkflowStore workflow,
    IAccessDecisionService access,
    Reporting.ReportSnapshotSync reports,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock,
    IAuditWriter audit)
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
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може затверджувати.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var key = new PeriodKey(periodKey);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        var decision = await access.CanApproveAsync(profile, documentId, sheetDefId, key, ct)
                                   .ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            throw new AccessDeniedException(
                "ECR-ACCS-0403",
                $"Затвердження аркуша {sheetDefId} відхилено: {decision.Reason}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-ACCS-0403.approveDenied",
                    ["sheetDefId"] = sheetDefId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["reason"] = decision.Reason.ToString(),
                });
        }

        // ⛔ `DAT-06`. Стан аркуша, запис у аудит і перерахунок статусу зрізу —
        // ОДНИМ комітом. Транзакції тут не було зовсім, і це не «на всяк
        // випадок»: `IAuditWriter` пише сирим `INSERT` по тому самому
        // підключенню й поза транзакцією комітить одразу
        // (`AuditWriter.CreateCommand`). Тобто `ApprovalStepPassed` лягав у
        // `aud.SecurityEvent` ОКРЕМИМ комітом, ще до `SaveChangesAsync`, —
        // і будь-який збій нижче лишав у журналі підпис під кроком, якого не
        // сталося. Проміжні підписи заводять саме заради відповідальності
        // (див. коментар нижче), і журнал, що розходиться зі станом, її не
        // фіксує, а підробляє.
        //
        // ⚠ `ExecuteInTransactionAsync` приєднується до вже відкритої
        // зовнішньої транзакції (`UnitOfWork.cs:174-178`), тож це обгортка, а
        // не переробка.
        await uow.ExecuteInTransactionAsync(
            innerCt => ApproveCoreAsync(documentId, sheetDefId, periodKey, key, approved, reason, userId, innerCt),
            ct).ConfigureAwait(false);
    }

    /// <summary>Зміна стану, аудит проміжного кроку і статус зрізу — під транзакцією.</summary>
    private async Task ApproveCoreAsync(
        long documentId,
        int sheetDefId,
        int periodKey,
        PeriodKey key,
        bool approved,
        string? reason,
        int userId,
        CancellationToken ct)
    {
        var state = await workflow.GetOrCreateAsync(documentId, sheetDefId, key, ct).ConfigureAwait(false);
        var now = clock.UtcNow;

        // `BE-11`: стан ДО дії — для журналу переходів.
        var fromStatus = state.Status;

        if (approved)
        {
            // ⛔ Проміжний крок НЕ робить аркуш затвердженим (`ФВ-5.17`).
            // Документ, який став би `Approved` після першого підпису,
            // потрапив би у звітність для регулятора без решти погоджень
            // (`ФВ-10.11`) — а саме заради них маршрут і заводять.
            //
            // ⚠ Маршруту немає — `step` порожній, `nextStepId` теж, і
            // `ApproveStep` поводиться рівно як `Approve`: одноетапно, як було.
            var step = await access
                .CurrentApprovalStepAsync(documentId, sheetDefId, key, ct)
                .ConfigureAwait(false);

            state.ApproveStep(userId, now, step?.NextStepId);

            // `BE-11`: проміжний крок — окрема дія, стан лишається `Submitted`.
            var action = step is { NextStepId: not null } ? ApprovalAction.ApproveStep : ApprovalAction.Approve;
            await workflow.AddEventAsync(
                ApprovalEvent.For(state, fromStatus, action, userId, now, stepOrdinal: step?.Ordinal),
                ct).ConfigureAwait(false);

            // ⛔ ПРОМІЖНИЙ крок пишеться в аудит окремо. На рядку стану є лише
            // `ApprovedByUserId` — один; після маршруту з трьох кроків там
            // лишиться останній, а перших двох не буде НІДЕ. Багатоетапність
            // заводять саме заради відповідальності, і втратити її разом із
            // проміжними підписами означало б зробити механізм гіршим за його
            // відсутність.
            //
            // ⚠ Останній крок не дублюється: його автор уже на рядку стану.
            if (step is { NextStepId: not null })
            {
                await audit.WriteSecurityEventAsync(
                    new SecurityEventRecord(
                        ChangedAt: now,
                        EventType: "ApprovalStepPassed",
                        TargetUserId: null,
                        TargetRoleId: step.RoleId,
                        DetailsJson: System.Text.Json.JsonSerializer.Serialize(new
                        {
                            documentId,
                            sheetDefId,
                            periodKey,
                            step = step.Ordinal,
                            of = step.TotalSteps,
                        }),
                        ChangedByUserId: userId,
                        CorrelationId: currentUser.CorrelationId),
                    ct).ConfigureAwait(false);
            }
        }
        else
        {
            // Домен вимагає коментаря сам; тут лише переклад порожнього рядка
            // в null, щоб повідомлення було про суть, а не про пробіли.
            state.Reject(userId, reason ?? string.Empty, now);

            // ⛔ `BE-11`: причина відхилення живе ТУТ. На рядку стану її зітре
            // наступне ж подання (`ApprovalState.Submit`).
            await workflow.AddEventAsync(
                ApprovalEvent.For(state, fromStatus, ApprovalAction.Reject, userId, now, state.RejectedReason),
                ct).ConfigureAwait(false);
        }

        // ⛔ Статус ДОКУМЕНТА не чіпається: його немає (D-93). Зведений стан
        // рахується запитом по wf.ApprovalState, і скалярне поле рано чи пізно
        // показало б Approved там, де половина аркушів у Draft.

        // ⛔ А ось статус ЗРІЗУ перераховується (`H-23b`). Він успадковується
        // від даних (`D-65`), і без цього виклику регуляторна вʼюха назавжди
        // тримала б стан на момент побудови: аркуші затвердили, а звіт для
        // регулятора лишився чернетковим і в перелік не потрапив.
        await reports.RefreshAsync(documentId, key, ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
