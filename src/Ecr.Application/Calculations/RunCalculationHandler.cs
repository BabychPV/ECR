// src/Ecr.Application/Calculations/RunCalculationHandler.cs
using System.Globalization;
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
    Security.IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock,
    IRecalculationApprovalStore approvals,
    IAuditWriter audit,
    IJobQueue? queue = null,
    IJobLeaseContext? lease = null)
{
    // ⛔ Константи `RecalculateClosedPermission` тут більше немає (`A7-20`).
    // Вона оголошувала право `Calculation.RecalculateClosed`, якого немає в
    // каталозі `sec.Permission` і не було в контракті: видати його не міг
    // ніхто, а перевіряти його ніде й не пробували. Правило ФВ-9.7 тримається
    // не правом, а ПОГОДЖЕННЯМ (`approval` нижче) — і саме тому працює.

    /// <summary>Право на запуск перерахунку проєкту (`02-contracts.md` §9).</summary>
    /// <remarks>
    /// ⛔ Q-151 (аудит фази 1). До цього пакета обробник не мав жодної
    /// перевірки права взагалі — лише те, що запит автентифікований. Той самий
    /// клас дефекту, що й `A7-53`: право оголошене в контракті документного
    /// маршруту (<c>RecalculateDocumentHandler.Permission</c>), а маршрут
    /// проєкту не був підключений НІ до чого — тож перевіряти права не було де.
    /// Тепер обидва маршрути перевіряють те саме право: перерахунок є
    /// перерахунок, незалежно від того, одного документа він стосується чи всіх.
    /// </remarks>
    public const string Permission = "Calculation.Recalculate";

    /// <summary>Ставить прогін у чергу і повертає ідентифікатор задачі.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період; <c>null</c> — повний рік.</param>
    /// <param name="approvalId">
    /// Погодження на перерахунок закритого періоду (<c>calc.RecalculationApproval</c>);
    /// <c>null</c> — немає.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-4221</c> — закритий період без придатного погодження.
    /// </exception>
    public async Task<string> HandleAsync(
        int projectId, int? periodKey, long? approvalId, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ Q-238: те саме, чого бракувало документному перерахунку до Q-174
        // (`RecalculateDocumentHandler`) — глобального `Calculation.Recalculate`
        // самого по собі недостатньо, коли ціль запиту (тут — projectId) не
        // прив'язана до викликача жодним грантом. До цього фіксу власник
        // проєкту A з правом Calculation.Recalculate міг перерахувати ЦІЛИЙ
        // чужий проєкт B (усі документи, увесь рік) без жодного гранта на B.
        // Поріг — `RecalculationApprovalPolicy.InitiatorGrant` (нині Read, як і
        // `CanReadDocumentAsync`): той самий, що й для запиту погодження, і
        // заданий в ОДНОМУ місці (аудит S1, питання S13).
        RecalculationApprovalPolicy.RequireProjectGrant(profile, projectId, RecalculationApprovalPolicy.InitiatorGrant);

        // ⛔ ФВ-6.14: право — у ЦЬОМУ проєкті.
        Security.PermissionCheck.RequireIn(profile, Permission, projectId);

        var userId = currentUser.UserId
            ?? throw new AccessDeniedException(
                "ECR-AUTH-0401", "Анонімний запит не запускає розрахунок.",
                new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var targets = await periods
            .GetPeriodStatesAsync(projectId, periodKey, ct)
            .ConfigureAwait(false);

        if (targets.Count == 0)
        {
            throw new NotFoundException(
                "ECR-PRD-0404", $"Періоду {periodKey} у проєкті {projectId} немає.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0404.periodForProject",
                    ["periodKey"] = periodKey?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        // ⛔ ЗАКРИТІ ПЕРІОДИ автоматично не перераховуються НІКОЛИ (ФВ-9.7).
        // Це не обережність: перерахунок закритого періоду змінює числа, які
        // вже подані регулятору, і робить це без жодного сліду в самих даних.
        //
        // ⚠ Тут лишається лише перевірка ЯКОСТІ погодження (`ConsumeApprovalAsync`)
        // — рішення «чи можна писати в цей період» більше НЕ живе в
        // тілі цього обробника, а взяте з `RecalculationWritePolicy`. Доти
        // воно жило саме тут і тільки тут, тож два інші маршрути до задачі
        // перерахунку (документ і нічний розклад) писали в закриті періоди
        // мовчки: вони цього обробника не кличуть узагалі.
        //
        // ⛔ Аудит безпеки S1: «друга людина» більше не число з тіла запиту.
        // Погодження — окремий запис, підтверджений ІНШОЮ людиною під її
        // власною сесією (`RecalculationApprovalHandlers.ConfirmAsync`), і тут
        // воно лише ВИКОРИСТОВУЄТЬСЯ — один раз, нижче, після всіх перевірок.
        var closed = targets.Where(p => p.State == PeriodState.Closed).ToList();
        var hasApproval = closed.Count > 0 && approvalId is not null;

        // ⛔ Поданий зріз не перераховується взагалі (ФВ-9.17) — навіть із
        // погодженням. Потреба змінити подану цифру закривається Reopen, який
        // лишає слід у робочому процесі, а не тихим перерахунком.
        //
        // ⛔ Правило НЕ знімається наявністю `approval` — і це не недогляд, а
        // суть (аудит 2026-09-16, §1.1). До цього стояло `submitted && approval
        // is null`, тож ОДНЕ погодження, видане на ОДИН закритий період,
        // знімало захист поданих зрізів у ВСІХ періодах запиту: `periodKey:
        // null` — це весь рік, і `targets` охоплює багато періодів одночасно.
        // Сценарій: 202601 закритий (погодження законне), 202603 відкритий із
        // поданими аркушами — і 202603 тихо перераховувався. Тепер цю різницю
        // тримає сама політика: `hasClosedPeriodApproval` знімає лише
        // `PeriodClosed`, а `SheetsSubmitted` — ніколи.
        foreach (var period in targets)
        {
            var submitted = await workflow
                .HasSubmittedSheetsAsync(projectId, new Domain.ValueObjects.PeriodKey(period.PeriodKey), ct)
                .ConfigureAwait(false);

            var denial = RecalculationWritePolicy.Check(period.State, submitted, hasApproval);
            if (denial != RecalculationWriteDenial.None)
            {
                throw RecalculationWritePolicy.Reject(denial, period.PeriodKey);
            }
        }

        var approval = hasApproval
            ? await ConsumeApprovalAsync(approvalId!.Value, projectId, periodKey, userId, ct).ConfigureAwait(false)
            : null;

        var jobId = await jobs
            .EnqueueAsync<IRecalculationJob>(
                new
                {
                    projectId,
                    periodKey,
                    triggeredByUserId = userId,
                    approvedBy = approval?.ConfirmedByUserId,
                    approvalReason = approval?.Reason,
                    approvalId = approval?.Id,
                    requestedAt = clock.UtcNow,
                },
                ct)
            .ConfigureAwait(false);

        if (approval is not null)
        {
            await RecalculationApprovalHandlers.WriteAuditAsync(
                audit, currentUser, clock.UtcNow, RecalculationApprovalHandlers.UsedEventType,
                approval.Id, projectId, approval.PeriodKey, userId,
                new { jobId, reason = approval.Reason, confirmedByUserId = approval.ConfirmedByUserId },
                ct).ConfigureAwait(false);
        }

        return jobId;
    }

    /// <summary>
    /// Завершує прогін: пише профіль і робить його актуальним.
    /// </summary>
    /// <param name="calculationRunId">Прогін.</param>
    /// <param name="profile">Профіль по модулях.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <param name="carryOver">
    /// RC14 (P2-4): прогін області аркуша — результати методологій інших аркушів, які переносяться з
    /// попереднього актуального прогону; <c>null</c> — прогін повний.
    /// </param>
    /// <remarks>
    /// ⚠ Обидві дії — ОДНИМ викликом сховища і однією транзакцією (ФВ-9.11).
    /// Між зняттям актуальності зі старого прогону і встановленням новому
    /// існує стан, у якому актуальних прогонів нуль або два; звіт, побудований
    /// у цю мить, не має правильної відповіді.
    /// <para>
    /// Профіль пишеться ЗАВЖДИ, зокрема порожній: бюджет 10 хвилин — вимога,
    /// і прогін без профілю нічого не каже про те, куди пішов час (J-1).
    /// </para>
    /// <para>
    /// ⛔ Fencing видимості (MI-02, правка «Аудиту» 1). Задачу з черги в базі
    /// (<see cref="IJobLeaseContext.Current"/> задано) могли перехопити, поки
    /// вона рахувала: оренду прострочено, інший виконавець уже рахує те саме.
    /// Тоді перемикання актуальності — у ТІЙ САМІЙ транзакції, що й
    /// <see cref="IJobQueue.FenceAsync"/>: X-лок на рядок задачі до коміту, і
    /// втрачена оренда (<c>false</c>) відкочує все — результат виконавця без
    /// оренди НЕ стає видимим (<see cref="JobLeaseLostException"/>).
    /// Шлях Quartz/HTTP (оренди немає) — як і був.
    /// </para>
    /// </remarks>
    /// <exception cref="JobLeaseLostException">Оренду задачі втрачено; нічого не записано.</exception>
    public async Task CompleteAsync(
        long calculationRunId, ModuleProfile profile, CancellationToken ct, ResultCarryOver? carryOver = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (lease?.Current is not { } claim || queue is null)
        {
            // ⛔ Транзакція й без оренди: сховище знімає актуальність зі старих
            // прогонів ОКРЕМИМ UPDATE до того, як зробити актуальним цей
            // (`SwitchCurrentRunAsync` — порядок явний, не на порядку UPDATE EF).
            // Без спільної транзакції між двома кроками був би коміт, після якого
            // актуальних прогонів нуль (ФВ-9.11).
            await uow.ExecuteInTransactionAsync(
                    token => SwitchAsync(calculationRunId, profile, carryOver, token), ct)
                .ConfigureAwait(false);
            return;
        }

        await uow.ExecuteInTransactionAsync(
                async token =>
                {
                    if (!await queue.FenceAsync(claim, token).ConfigureAwait(false))
                    {
                        throw new JobLeaseLostException(claim.JobId);
                    }

                    await SwitchAsync(calculationRunId, profile, carryOver, token).ConfigureAwait(false);
                },
                ct)
            .ConfigureAwait(false);
    }

    private async Task SwitchAsync(
        long calculationRunId, ModuleProfile profile, ResultCarryOver? carryOver, CancellationToken ct)
    {
        // RC14 (P2-4): прогін області не перераховував інші аркуші — їхні результати переносяться з
        // попереднього актуального прогону ДО перемикання актуальності, у тій самій транзакції.
        if (carryOver is { MethodologyIds.Count: > 0 })
        {
            await results
                .CarryOverResultsAsync(calculationRunId, carryOver.DocumentId, carryOver.MethodologyIds, ct)
                .ConfigureAwait(false);
        }

        await results
            .SwitchCurrentRunAsync(calculationRunId, profile.ToJson(), ct)
            .ConfigureAwait(false);

        var invalidated = await results
            .InvalidateReportSnapshotsAsync(calculationRunId, ct)
            .ConfigureAwait(false);

        // ⚠ Журнал — у ТІЙ САМІЙ транзакції, що й перемикання (C4): відкат
        // перемикання не лишає запису «зріз застарів», а запис не губиться,
        // якщо перемикання закомітилось.
        var utcNow = clock.UtcNow;
        foreach (var snapshot in invalidated)
        {
            await audit
                .WriteStructureChangeAsync(SnapshotInvalidatedRecord(snapshot, calculationRunId, utcNow), ct)
                .ConfigureAwait(false);
        }

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Тип сутності в журналі для застарілості зрізу (ФВ-10.5).</summary>
    public const string SnapshotAuditEntityType = "ReportSnapshot";

    /// <summary>Операція журналу: зріз застарів після перерахунку (ФВ-10.5).</summary>
    public const string SnapshotInvalidatedOperation = "Invalidated";

    /// <summary>Автор системної події — та сама умовність, що й у <c>RecalculationService</c>.</summary>
    private const int SystemUserId = 0;

    /// <summary>Запис журналу «зріз застарів» (ФВ-10.5).</summary>
    /// <remarks>
    /// ⚠ Автор — система, а не той, хто запустив перерахунок: зріз застарів
    /// як наслідок, а не як його дія, і так само застаріває після нічного
    /// розкладу, де людини немає. Хто запустив — видно за прогоном
    /// (<c>calculationRunId</c> у <c>NewJson</c> і в <c>CorrelationId</c>).
    /// <para>
    /// <c>ChangeClass.Safe</c>: вміст зрізу не змінюється — змінюється лише те,
    /// що про нього можна сказати.
    /// </para>
    /// </remarks>
    private static StructureChangeRecord SnapshotInvalidatedRecord(
        InvalidatedReportSnapshot snapshot, long calculationRunId, DateTime utcNow)
        => new(
            utcNow,
            TemplateVersionId: snapshot.TemplateVersionId,
            EntityType: SnapshotAuditEntityType,
            EntityId: checked((int)snapshot.SnapshotId),
            ChangeClass: ChangeClass.Safe,
            Operation: SnapshotInvalidatedOperation,
            OldJson: System.Text.Json.JsonSerializer.Serialize(new { isStale = false }),
            NewJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                isStale = true,
                calculationRunId,
                reportVersionId = snapshot.ReportVersionId,
                projectId = snapshot.ProjectId,
                periodKey = snapshot.PeriodKey,
                status = snapshot.Status,
                builtAt = snapshot.BuiltAt,
            }),
            ChangeReason: "calculation-run-current",
            ChangedByUserId: SystemUserId,
            CorrelationId: string.Create(CultureInfo.InvariantCulture, $"calc-run:{calculationRunId}"));

    /// <summary>
    /// Використовує погодження: атомарно, один раз, лише своє, лише на цей
    /// проєкт і період, лише підтверджене й не прострочене.
    /// </summary>
    /// <remarks>
    /// ⚠ «Погодження є / погодження немає» вирішує
    /// <see cref="RecalculationWritePolicy"/> разом зі станом періоду; тут —
    /// чи є погодження справжнім. Використання стоїть ПІСЛЯ політики: відмова
    /// через поданий зріз не має спалювати законне погодження.
    /// <para>
    /// ⛔ <paramref name="periodKey"/> <c>null</c> (увесь рік) не збігається з
    /// жодним погодженням: воно відкриває один період, а не рік.
    /// </para>
    /// </remarks>
    private async Task<RecalculationApprovalDto> ConsumeApprovalAsync(
        long approvalId, int projectId, int? periodKey, int userId, CancellationToken ct)
    {
        var used = periodKey is { } key
            ? await approvals.TryConsumeAsync(approvalId, projectId, key, userId, clock.UtcNow, ct).ConfigureAwait(false)
            : null;

        return used ?? throw new BusinessRuleException(
            "ECR-CALC-4221",
            $"Погодження {approvalId} не придатне для цього перерахунку.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CALC-4221.approvalNotUsable",
                ["approvalId"] = approvalId.ToString(CultureInfo.InvariantCulture),
            });
    }
}
