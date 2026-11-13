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
    IAuditWriter audit,
    IJobQueue? queue = null,
    IJobLeaseContext? lease = null,
    ICalculationIssueStore? issues = null)
{
    // ⛔ Константи `RecalculateClosedPermission` тут більше немає (`A7-20`): права
    // `Calculation.RecalculateClosed` немає в каталозі `sec.Permission`. ✎ 07.11 (D-468): і погодження
    // перерахунку закритого періоду (аудит S1) прибрано — закритий місяць має ОДИН шлях: Reopen (з причиною)
    // → звичайний перерахунок. ФВ-9.7 тримає `RecalculationWritePolicy` без винятків.

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
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-4221</c> <c>periodClosed</c> — закритий період (шлях — Reopen, D-468).
    /// </exception>
    public async Task<string> HandleAsync(int projectId, int? periodKey, CancellationToken ct)
    {
        var outcome = await RequestAsync(projectId, periodKey, ct).ConfigureAwait(false);

        // ⚠ Старий виклик (без переліку): «рахувати нічого» лишається відмовою 4221, як і до UAT-recalc-ux.
        return outcome.JobId
            ?? throw RecalculationWritePolicy.Reject(RecalculationWriteDenial.SheetsSubmitted, periodKey ?? 0);
    }

    /// <summary>
    /// UAT-recalc-ux: ставить прогін у чергу, ПРОПУСКАЮЧИ затверджені аркуші, і повертає їх перелік.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період; <c>null</c> — повний рік.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Задача і пропущені (затверджені) аркуші; <c>JobId = null</c> — у названому періоді всі аркуші всіх документів
    /// затверджені, рахувати нічого (не помилка: людина бачить перелік і куди перейти).
    /// </returns>
    /// <exception cref="BusinessRuleException">
    /// <c>ECR-CALC-4221</c> <c>periodClosed</c> — у запиті є закритий період (шлях — Reopen, D-468).
    /// </exception>
    /// <remarks>
    /// ⛔ Слово людини 07.11: «тільки погоджені не мають перераховуватися». Затверджений аркуш не перераховується,
    /// поданий — перераховується й позначається для погоджувача; захист — ПО АРКУШУ (тримає задача:
    /// <c>RecalculationJob.SubmittedSheetsAsync</c> / <c>GuardCompletionAsync</c>, а розклад проєкту не ставить
    /// документ, у якому затверджено все), а не відмовою всьому проєкту. Відмова цілим запитом була глухим кутом:
    /// людина не бачила, які саме аркуші заважають (UAT 07.11).
    /// </remarks>
    public async Task<RecalculationRequestOutcome> RequestAsync(int projectId, int? periodKey, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ Q-238: глобального `Calculation.Recalculate` самого по собі недостатньо, коли ціль запиту
        // (projectId) не прив'язана до викликача жодним грантом: власник проєкту A міг перерахувати ЦІЛИЙ
        // чужий проєкт B. Поріг — `RecalculationGrantPolicy.InitiatorGrant` (нині Read), в ОДНОМУ місці.
        RecalculationGrantPolicy.RequireProjectGrant(profile, projectId, RecalculationGrantPolicy.InitiatorGrant);

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

        // ⛔ B69: архівований (чи той, що архівується) проєкт не перераховується взагалі — ані роком, ані названим
        // періодом. Без цього рядка річний запит вилучав закриті періоди й ставив задачу, а виняток «місяць відкрито для
        // документа», що пережив архівацію, пускав її писати в архів (`RecalculationJob.RefusedPeriodsAsync` тепер теж
        // відмовляє — тут швидка відмова для людини, а не другий гейт).
        if (await periods.IsProjectArchivedOrArchivingAsync(projectId, ct).ConfigureAwait(false))
        {
            throw RecalculationWritePolicy.Reject(RecalculationWriteDenial.ProjectArchived, periodKey ?? 0);
        }

        // ⛔ ЗАКРИТІ ПЕРІОДИ не перераховуються НІКОЛИ (ФВ-9.7): перерахунок закритого періоду змінює числа,
        // які вже подані регулятору. ✎ 07.11 (D-468): винятку «за погодженням» більше немає — закритий місяць
        // спершу відкривають повторно (Reopen з причиною лишає слід у робочому процесі), далі звичайний перерахунок.
        // Рішення — у `RecalculationWritePolicy` (той самий гейт тримають маршрут документа і задача).
        //
        // ✎ UAT-recalc-ux (07.11): поданий аркуш НЕ відмовляє всьому проєкту — його пропускає задача (по
        // аркушу), а запит повертає перелік пропущених. Тут лишається лише відмова закритого періоду.
        //
        // ✎ UAT-recalc-ux (e2e 07.11): перерахунок за ВЕСЬ рік (`periodKey: null`, кнопка Periods) закритий період
        // НЕ відмовляє — він вилучається зі скоупу, рівно як це вже робить задача (`RecalculationJob.RefusedPeriodsAsync`:
        // «запит не називав закритого періоду»). Відмова тут робила кнопку проєкту глухим кутом у будь-якому році після
        // першого закритого місяця. Названий закритий період — відмова (шлях — Reopen, D-468).
        var excluded = new HashSet<int>();
        foreach (var period in targets)
        {
            var denial = RecalculationWritePolicy.Check(period.State, hasSubmittedSheets: false);
            if (denial == RecalculationWriteDenial.None)
            {
                continue;
            }

            if (periodKey is null && denial == RecalculationWriteDenial.PeriodClosed)
            {
                excluded.Add(period.PeriodKey);
                continue;
            }

            throw RecalculationWritePolicy.Reject(denial, period.PeriodKey);
        }

        var (skipped, truncated) = await SkippedAsync(profile, projectId, periodKey, excluded, ct).ConfigureAwait(false);

        // Названий період, де затверджено ВСЕ: задачу не ставимо.
        if (periodKey is { } named
            && await workflow.AllSheetsApprovedAsync(projectId, named, ct).ConfigureAwait(false))
        {
            return new RecalculationRequestOutcome(null, skipped, truncated);
        }

        // ✎ UAT recalc-progress (08.11): ціль `project{id}-…` — за нею перелік задач знаходить перерахунок проєкту
        // (`GET /jobs?projectId=&kind=projectRecalculation`), і сторінки відновлюють банер прогресу після переходу.
        // Злиття, а не нова задача поруч: повторне «Перерахувати», поки розклад ще чекає, — та сама робота.
        var jobId = await jobs
            .EnqueueCoalescedAsync<IRecalculationJob>(
                ProjectTargetOf(projectId, periodKey),
                new
                {
                    projectId,
                    periodKey,
                    triggeredByUserId = userId,
                    requestedAt = clock.UtcNow,
                },
                ct,

                // ⛔ Автор задачі: без нього батько не потрапляв у «Мої задачі» (`mine=true`), хоча 92 його діти —
                // потрапляли (їх ставить `FanOutAsync` з `TriggeredByUserId`).
                userId)
            .ConfigureAwait(false);

        return new RecalculationRequestOutcome(jobId, skipped, truncated);
    }

    /// <summary>
    /// Ціль перерахунку проєкту: <c>project{id}-year</c> (повний рік) або <c>project{id}-p{periodKey}</c>.
    /// </summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період; <c>null</c> — повний рік.</param>
    /// <remarks>
    /// ⚠ Префікс <c>project{id}-</c> — той, за яким перелік задач шукає перерахунок проєкту
    /// (<see cref="JobListKinds.ProjectTargetPrefix"/>); перерахунок після переносу версії
    /// (<c>MigrationRecalculation.TargetOf</c>, <c>project{id}-migrate-recalc</c>) має той самий префікс.
    /// </remarks>
    public static string ProjectTargetOf(int projectId, int? periodKey)
        => periodKey is { } key
            ? string.Create(CultureInfo.InvariantCulture, $"project{projectId}-p{key}")
            : string.Create(CultureInfo.InvariantCulture, $"project{projectId}-year");

    /// <summary>
    /// Пропущені аркуші проєкту — лише ті, що читач бачить: схований від нього аркуш (звуження ролі, Deny) не
    /// називається ні кодом, ні станом (S18).
    /// </summary>
    private async Task<(IReadOnlyList<SkippedSheet> Items, bool Truncated)> SkippedAsync(
        Security.AccessProfile profile, int projectId, int? periodKey, HashSet<int> excludedPeriods,
        CancellationToken ct)
    {
        var rows = await workflow
            .ListApprovedSheetsAsync(projectId, documentId: null, periodKey, RecalculationSkips.MaxListed + 1, ct)
            .ConfigureAwait(false);

        // Закриті періоди річного запиту поза скоупом узагалі — їхні аркуші не «пропущені як затверджені».
        if (excludedPeriods.Count > 0)
        {
            rows = [.. rows.Where(r => !excludedPeriods.Contains(r.PeriodKey))];
        }

        if (Documents.DocumentSheetVisibility.HasRestrictions(profile))
        {
            var visible = new List<SkippedSheet>(rows.Count);
            foreach (var group in rows.GroupBy(r => r.DocumentId))
            {
                var scope = await access.ReadScopeAsync(profile, group.Key, ct).ConfigureAwait(false);
                visible.AddRange(group.Where(r =>
                    scope.InPeriod(new Domain.ValueObjects.PeriodKey(r.PeriodKey)).CanReadSheet(r.SheetDefId)));
            }

            rows = visible;
        }

        return RecalculationSkips.Cap(rows);
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
    /// <param name="beforeSwitch">
    /// D2-03: перевірка, що виконується першою в транзакції перемикання; виняток відкочує все.
    /// <c>null</c> — без перевірки.
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
    /// <para>
    /// ⛔ D2-03: <paramref name="beforeSwitch"/> — перевірка викликача (стан аркушів і
    /// періоду), що виконується ПЕРШОЮ в тій самій транзакції, що й перемикання.
    /// Рішення «що рахувати» задача ухвалила на старті, а методологічна фаза триває
    /// хвилини; відмова тут відкочує все, і попередній прогін лишається актуальним.
    /// </para>
    /// <exception cref="JobLeaseLostException">Оренду задачі втрачено; нічого не записано.</exception>
    public async Task CompleteAsync(
        long calculationRunId, ModuleProfile profile, CancellationToken ct,
        IReadOnlyCollection<ResultCarryOver>? carryOver = null,
        Func<CancellationToken, Task>? beforeSwitch = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (lease?.Current is not { } claim || queue is null)
        {
            // ⛔ Транзакція й без оренди: сховище знімає актуальність зі старих
            // прогонів ОКРЕМИМ UPDATE до того, як зробити актуальним цей
            // (`SwitchCurrentRunAsync` — порядок явний, не на порядку UPDATE EF).
            // Без спільної транзакції між двома кроками був би коміт, після якого
            // актуальних прогонів нуль (ФВ-9.11).
            IReadOnlyList<SupersededRun> unleased = [];
            await uow.ExecuteInTransactionAsync(
                    async token => unleased = await SwitchAsync(calculationRunId, profile, carryOver, beforeSwitch, token)
                        .ConfigureAwait(false),
                    ct)
                .ConfigureAwait(false);
            await PurgeSupersededAsync(unleased, ct).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<SupersededRun> superseded = [];
        await uow.ExecuteInTransactionAsync(
                async token =>
                {
                    if (!await queue.FenceAsync(claim, token).ConfigureAwait(false))
                    {
                        throw new JobLeaseLostException(claim.JobId);
                    }

                    superseded = await SwitchAsync(calculationRunId, profile, carryOver, beforeSwitch, token)
                        .ConfigureAwait(false);
                },
                ct)
            .ConfigureAwait(false);
        await PurgeSupersededAsync(superseded, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Q-17 (рішення людини 28.10): після коміту перемикання прибирає рядки прогонів, що перестали бути актуальними.
    /// </summary>
    /// <remarks>
    /// ⛔ ПІСЛЯ коміту й поза транзакцією: до коміту ті прогони ще актуальні для читачів, а видалення десятків тисяч
    /// рядків усередині перемикання тримало б замок рядка задачі й відкотило б саме перемикання при збої. Збій
    /// прибирання прогін НЕ валить: перемикання вже правильне (рядки неактуальних прогонів не читає ніхто), решту
    /// доїсть нічна задача <c>SupersededResultsSweepJob</c>. Скасування проходить нагору.
    /// </remarks>
    private async Task PurgeSupersededAsync(IReadOnlyList<SupersededRun> superseded, CancellationToken ct)
    {
        if (superseded is not { Count: > 0 })
        {
            return;
        }

        try
        {
            await results.PurgeSupersededRunsAsync(superseded, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сховище само пише збій у журнал і не кидає; сюди дійде лише непередбачене. Прогін уже актуальний.
        }
    }

    private async Task<IReadOnlyList<SupersededRun>> SwitchAsync(
        long calculationRunId, ModuleProfile profile, IReadOnlyCollection<ResultCarryOver>? carryOver,
        Func<CancellationToken, Task>? beforeSwitch, CancellationToken ct)
    {
        if (beforeSwitch is not null)
        {
            await beforeSwitch(ct).ConfigureAwait(false);
        }

        // RC14 (P2-4): прогін області не перераховував інші аркуші — їхні результати переносяться з
        // попереднього актуального прогону ДО перемикання актуальності, у тій самій транзакції.
        // RC15 (P2-C): так само для пропущених (поданих/затверджених) аркушів перерахунку документа; на кожен
        // документ прогону — окремий запис (послідовний прогін кількох документів).
        foreach (var item in carryOver ?? [])
        {
            if (item.MethodologyIds.Count > 0)
            {
                await results
                    .CarryOverResultsAsync(calculationRunId, item.DocumentId, item.MethodologyIds, ct)
                    .ConfigureAwait(false);
            }
        }

        var superseded = await results
            .SwitchCurrentRunAsync(calculationRunId, profile.ToJson(), ct)
            .ConfigureAwait(false);

        // AN-69: поточні проблеми проходу - у ТІЙ САМІЙ транзакції, що й результати й перемикання. Прогін, відмовлений
        // як старіший за актуальний (`Superseded`), нічого не замінює: його прохід застарів.
        if (issues is not null && !superseded.Any(s => s.RunId == calculationRunId))
        {
            _ = await issues
                .ReplaceAsync(
                    calculationRunId,
                    [.. profile.EvaluatedTables],
                    [
                        .. profile.UnmatchedRows.Select(r => new CalculationIssueInput(
                            r.TableInstanceId, r.RowNumber, r.RowKey, r.MethodologyId)),
                        // ✎ 10.11 (doc 95): відхилений правилом категорії рядок — теж проблема рядка, а не лише повідомлення задачі.
                        .. profile.RejectedRows
                            .DistinctBy(r => (r.TableInstanceId, r.RowKey, r.MethodologyId))
                            .Select(r => new CalculationIssueInput(
                                r.TableInstanceId, r.RowNumber, r.RowKey, r.MethodologyId,
                                ICalculationIssueStore.CategoryRuleRejected)),
                        // AN-164: рядок без значення через відсутні аргументи формул — проблема рядка, а не тихий нуль.
                        .. profile.MissingInputRows
                            .DistinctBy(r => (r.TableInstanceId, r.RowKey, r.MethodologyId))
                            .Select(r => new CalculationIssueInput(
                                r.TableInstanceId, r.RowNumber, r.RowKey, r.MethodologyId,
                                ICalculationIssueStore.MissingInputs)),
                    ],
                    ct)
                .ConfigureAwait(false);
        }

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
        return superseded;
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
}
