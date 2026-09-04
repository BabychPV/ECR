// src/Ecr.Application/Calculations/RunCalculationHandler.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations;

/// <summary>
/// Прогін розрахунку. **Бюджет повного року — 10 хвилин** (ПРД-13, D-63)
/// проти 20 у чинній системі.
/// </summary>
/// <remarks>
/// Це вимога, а не результат заміру: профіль по модулях (`J-1`) показує,
/// звідки взяти різницю. Тому <c>ModulesProfileJson</c> заповнюється завжди,
/// а не лише в діагностичному режимі.
/// </remarks>
public sealed class RunCalculationHandler(
    IPeriodStore periods,
    IWorkflowStore workflow,
    ICalculationResultStore results,
    IBackgroundJobScheduler jobs,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право на перерахунок закритого періоду (ФВ-9.7).</summary>
    public const string RecalculateClosedPermission = "Calculation.RecalculateClosed";

    /// <summary>Ставить прогін у чергу і повертає ідентифікатор задачі.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період; <c>null</c> — повний рік.</param>
    /// <param name="approval">Погодження на перерахунок закритого періоду; <c>null</c> — немає.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-4221</c> — закритий період без погодження.
    /// </exception>
    public async Task<string> HandleAsync(
        int projectId, int? periodKey, ClosedPeriodApproval? approval, CancellationToken ct)
    {
        var userId = currentUser.UserId
            ?? throw new AccessDeniedException("ECR-AUTH-0401", "Анонімний запит не запускає розрахунок.");

        var targets = await periods
            .GetPeriodStatesAsync(projectId, periodKey, ct)
            .ConfigureAwait(false);

        if (targets.Count == 0)
        {
            throw new NotFoundException(
                "ECR-PRD-0404", $"Періоду {periodKey} у проєкті {projectId} немає.");
        }

        // ⛔ ЗАКРИТІ ПЕРІОДИ автоматично не перераховуються НІКОЛИ (ФВ-9.7).
        // Це не обережність: перерахунок закритого періоду змінює числа, які
        // вже подані регулятору, і робить це без жодного сліду в самих даних.
        var closed = targets.Where(p => p.State == PeriodState.Closed).ToList();
        if (closed.Count > 0)
        {
            RequireApproval(closed.Select(p => p.PeriodKey).ToList(), approval);
        }

        // ⛔ Поданий зріз не перераховується взагалі (ФВ-9.17) — навіть із
        // погодженням. Потреба змінити подану цифру закривається Reopen, який
        // лишає слід у робочому процесі, а не тихим перерахунком.
        foreach (var period in targets)
        {
            var submitted = await workflow
                .HasSubmittedSheetsAsync(projectId, new Domain.ValueObjects.PeriodKey(period.PeriodKey), ct)
                .ConfigureAwait(false);

            if (submitted && approval is null)
            {
                throw new BusinessRuleException(
                    "ECR-CALC-4221",
                    $"Період {period.PeriodKey} має подані аркуші: перерахунок змінив би числа, "
                    + "які вже пішли на погодження. Штатний шлях — Reopen.");
            }
        }

        return await jobs
            .EnqueueAsync<IRecalculationJob>(
                new
                {
                    projectId,
                    periodKey,
                    triggeredByUserId = userId,
                    approvedBy = approval?.ApprovedByUserId,
                    approvalReason = approval?.Reason,
                    requestedAt = clock.UtcNow,
                },
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Завершує прогін: пише профіль і робить його актуальним.
    /// </summary>
    /// <param name="calculationRunId">Прогін.</param>
    /// <param name="profile">Профіль по модулях.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <remarks>
    /// ⚠ Обидві дії — ОДНИМ викликом сховища і однією транзакцією (ФВ-9.11).
    /// Між зняттям актуальності зі старого прогону і встановленням новому
    /// існує стан, у якому актуальних прогонів нуль або два; звіт, побудований
    /// у цю мить, не має правильної відповіді.
    /// <para>
    /// Профіль пишеться ЗАВЖДИ, зокрема порожній: бюджет 10 хвилин — вимога,
    /// і прогін без профілю нічого не каже про те, куди пішов час (J-1).
    /// </para>
    /// </remarks>
    public async Task CompleteAsync(long calculationRunId, ModuleProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await results
            .SwitchCurrentRunAsync(calculationRunId, profile.ToJson(), ct)
            .ConfigureAwait(false);

        await results.InvalidateReportSnapshotsAsync(calculationRunId, ct).ConfigureAwait(false);
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Перевіряє погодження на перерахунок закритих періодів.</summary>
    private void RequireApproval(IReadOnlyList<int> closedKeys, ClosedPeriodApproval? approval)
    {
        if (approval is null)
        {
            throw new BusinessRuleException(
                "ECR-CALC-4221",
                $"Закриті періоди ({string.Join(", ", closedKeys)}) не перераховуються автоматично: "
                + "потрібне окреме погодження (ФВ-9.7).",
                new Dictionary<string, object?> { ["closedPeriods"] = closedKeys });
        }

        // ⚠ Погодження ≠ «прапорець у запиті». Причина обов'язкова, і той, хто
        // погодив, має бути іншою людиною, ніж та, що запускає: інакше
        // «окреме погодження» звелося б до зайвого поля у формі.
        if (string.IsNullOrWhiteSpace(approval.Reason))
        {
            throw new BusinessRuleException(
                "ECR-CALC-4221",
                "Погодження перерахунку закритого періоду без причини не приймається.");
        }

        if (approval.ApprovedByUserId == currentUser.UserId)
        {
            throw new BusinessRuleException(
                "ECR-CALC-0409",
                "Погодити власний перерахунок закритого періоду не можна (правило чотирьох очей, D-40).");
        }
    }
}

/// <summary>
/// Погодження на перерахунок закритого періоду (ФВ-9.7).
/// </summary>
/// <param name="ApprovedByUserId">Хто погодив; не той, хто запускає.</param>
/// <param name="Reason">Причина; обов'язкова і потрапляє в аудит.</param>
public sealed record ClosedPeriodApproval(int ApprovedByUserId, string Reason);
