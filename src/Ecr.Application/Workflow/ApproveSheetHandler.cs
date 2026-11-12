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
    IAuditWriter audit,
    IDocumentStore documents,

    // ✎ D-442: блокування аркуша під затвердження і новий зріз після правки погоджувача. У проді вприснуте
    // завжди (`DependencyInjection`); `null` — лише тестові фабрики обробника без сховищ комірок.
    ApprovalSnapshotRefresh? snapshotRefresh = null,

    // ✎ D-451: лист «Аркуш повернуто» після відхилення — ПІСЛЯ коміту, без права валити дію; `null` — тестові
    // фабрики обробника без пошти.
    SheetWorkflowNotifier? notifier = null,
    // D-462: повернутий аркуш знову редагований — комірки наздоганяють сирий шар (SheetMaterializationTrigger);
    // `null` — тестові фабрики обробника без черги.
    IMaterializationScheduler? materialization = null)
{
    /// <summary>Затверджує або відхиляє аркуш.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="approved"><c>true</c> — затвердити, <c>false</c> — відхилити.</param>
    /// <param name="reason">Коментар; обов'язковий при відхиленні (ФВ-5.15).</param>
    /// <param name="ct">Токен скасування.</param>
    public Task HandleAsync(
        long documentId, int sheetDefId, int periodKey, bool approved, string? reason, CancellationToken ct)
        => HandleAsync(documentId, sheetDefId, periodKey, approved, reason, sheetCopyId: null, ct);

    /// <summary>Затверджує або відхиляє аркуш чи його КОПІЮ (RS: власний стан копії за період).</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="approved"><c>true</c> — затвердити, <c>false</c> — відхилити.</param>
    /// <param name="reason">Коментар; обов'язковий при відхиленні (ФВ-5.15).</param>
    /// <param name="sheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(
        long documentId, int sheetDefId, int periodKey, bool approved, string? reason, long? sheetCopyId,
        CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може затверджувати.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        // B-16: спільний валідатор (`PeriodKey.Parse`), не первинний
        // конструктор — той не перевіряє нічого, і невірний період доходив
        // би до затвердження мовчки.
        var key = PeriodKey.Parse(periodKey);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);

        // ⛔ S2 / B-08: невидимий документ — `404`, як на кожному маршруті
        // документа, а не `403` з рішення про затвердження (той сам по собі каже,
        // що документ існує).
        await Documents.DocumentVisibility.RequireVisibleAsync(access, profile, documentId, ct).ConfigureAwait(false);

        // ⛔ Схований від читача аркуш — та сама відмова, що й аркуш поза складом, ДО гранта й стану:
        // «аркуш у стані Submitted, а не …» розповідало б про стан аркуша, якого читач не бачить.
        await Documents.DocumentVisibility
            .RequireSheetVisibleAsync(documents, access, profile, documentId, sheetDefId, key, ct).ConfigureAwait(false);

        // RS: копія має належати цьому аркушу документа — інакше та сама відмова, що й аркуш поза складом.
        if (sheetCopyId is { } copy && !await documents.HasSheetCopyAsync(documentId, sheetDefId, copy, ct).ConfigureAwait(false))
        {
            throw Documents.DocumentVisibility.SheetNotInDocument(documentId, sheetDefId);
        }

        // ⚠ Швидка відмова ДО транзакції — без блокувань і без читання стану.
        // Рішення, на яке справді спирається підпис, приймається ще раз
        // усередині транзакції (R5-W1 / W1-05, нижче).
        await RequireApproveAllowedAsync(profile, documentId, sheetDefId, key, sheetCopyId, ct).ConfigureAwait(false);

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
        //
        // ⛔ L6-15: стан аркуша читається ВСЕРЕДИНІ замикання. Стратегія повторів
        // (1205) виконує його вдруге; стан, прочитаний до транзакції й уже
        // змінений першою спробою (`ApproveStep`), на повторі дав би
        // `wrongState` замість затвердження.
        await uow.ExecuteInTransactionAsync(
            async innerCt =>
            {
                // ✎ D-442: ВИНЯТКОВЕ блокування аркуша — ПЕРШОЮ дією транзакції (порядок як у подання):
                // правка погоджувача, що вже пише, зафіксується до зрізу нижче, а нова чекатиме коміту
                // і під своїм спільним блокуванням побачить новий крок чи `Approved` — і відмову.
                if (snapshotRefresh is not null)
                {
                    await snapshotRefresh.EnterAsync(documentId, sheetDefId, key, innerCt, sheetCopyId).ConfigureAwait(false);
                }

                var state = await workflow.GetOrCreateOfAsync(documentId, sheetDefId, key, sheetCopyId, innerCt).ConfigureAwait(false);

                // ⛔ R5-W1 / W1-05 (TOCTOU): роль кроку маршруту — ПІСЛЯ читання
                // стану, у тій самій транзакції. Рішення до транзакції бачило
                // крок N; якщо інший погоджувач устиг закомітити крок N до
                // `GetOrCreateAsync`, тут уже читається крок N+1 зі свіжим
                // `RowVersion` — конфлікту немає, і погоджувач кроку N підписав
                // би крок N+1 (аж до остаточного `Approved` без директора).
                // Тепер: коміт ДО читання стану — рішення бачить новий крок і
                // відмовляє; коміт ПІСЛЯ — збереження падає на `RowVersion`
                // (409). На повторі після 1205 рішення теж перевиконується.
                await RequireApproveAllowedAsync(profile, documentId, sheetDefId, key, sheetCopyId, innerCt).ConfigureAwait(false);

                // ⚠ D-440: правило чотирьох очей (F-25, заборона самопогодження) скасоване безумовно —
                // той, хто подав, погоджує сам, якщо роль поточного кроку і грант це дають (рішення вище).
                // Слід лишається в журналі: `GetWorkflowHistoryHandler` позначає таке погодження.
                await ApproveCoreAsync(state, documentId, sheetDefId, periodKey, approved, reason, userId, sheetCopyId, profile, innerCt)
                    .ConfigureAwait(false);

                // ⛔ D-462: відхилення — аркуш знову редагований; черга в базі — постановка тут, останнім оператором.
                // RS (Q5): збір пише лише в оригінал — повернута копія наздоганяти сирий шар не має чого.
                if (!approved && sheetCopyId is null)
                {
                    await SheetMaterializationTrigger
                        .InsideTransactionAsync(materialization, documentId, sheetDefId, periodKey, innerCt)
                        .ConfigureAwait(false);
                }
            },
            ct).ConfigureAwait(false);

        if (!approved && sheetCopyId is null)
        {
            await SheetMaterializationTrigger
                .AfterCommitAsync(materialization, documentId, sheetDefId, periodKey, ct)
                .ConfigureAwait(false);
        }

        // ✎ D-451: відхилення (`Submitted → Rejected`, `ApprovalAction.Reject`) — повернення погоджувачем; лист
        // власнику й учасникам Write/Submit, за ввімкненим правилом, ПІСЛЯ коміту. Затвердження листа не дає.
        // RS: лист про повернення КОПІЇ — разом із контрактом копій (коміт 8).
        if (!approved && notifier is not null && sheetCopyId is null)
        {
            await notifier.ReturnedAsync(documentId, sheetDefId, key, userId, clock.UtcNow, reason, ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Повідомлення для тіла відмови затвердження через помилку формули.</summary>
    public const string FormulaErrorsMessageKey = "err.ECR-SUB-4221.formulaErrorsApprove";

    /// <summary>
    /// ✎ C-1: відмова затвердження — 422 <c>ECR-SUB-4221</c>; комірки під забороною погоджувача не називаються
    /// (S6, <c>HiddenValidationIssues</c>, як у поданні).
    /// </summary>
    private async Task ThrowFormulaErrorsAsync(
        IReadOnlyList<Validation.ValidationMessage> errors, AccessProfile profile, long documentId, PeriodKey key,
        CancellationToken ct)
    {
        var anyPeriod = await access.ReadScopeAsync(profile, documentId, ct).ConfigureAwait(false);
        var readable = anyPeriod.InPeriod(key);
        var shown = Validation.HiddenValidationIssues.ForViewer([.. errors], m => Validation.HiddenValidationIssues.CanSee(readable, m));
        var onlyHidden = shown.Count == 1 && Validation.HiddenValidationIssues.IsPlaceholder(shown[0]);

        throw new BusinessRuleException(
            Domain.Errors.ErrorCodes.SubmitBlocked,
            onlyHidden
                ? "Затвердження неможливе: є зауваження поза вашою видимістю."
                : $"Затвердження неможливе: помилок формул — {shown.Count}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = onlyHidden ? Validation.HiddenValidationIssues.MessageKey : FormulaErrorsMessageKey,
                ["messages"] = shown
                    .Select(m => (object)new
                    {
                        m.RuleCode,
                        m.Message,
                        m.RowKey,
                        m.ColumnCode,
                        MessageKey = Validation.HiddenValidationIssues.IsPlaceholder(m) ? Validation.HiddenValidationIssues.MessageKey : null,
                    })
                    .ToList(),
                ["messageCount"] = onlyHidden ? null : shown.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <summary>Рішення про затвердження (грант, стан, роль поточного кроку маршруту); відмова — 403.</summary>
    private async Task RequireApproveAllowedAsync(
        AccessProfile profile, long documentId, int sheetDefId, PeriodKey key, long? sheetCopyId, CancellationToken ct)
    {
        var decision = await access.CanApproveOfAsync(profile, documentId, sheetDefId, key, sheetCopyId, ct)
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
                    ["reasonKey"] = $"deny.{decision.Reason}",
                });
        }
    }

    /// <summary>Зміна стану, аудит проміжного кроку і статус зрізу — під транзакцією.</summary>
    private async Task ApproveCoreAsync(
        ApprovalState state,
        long documentId,
        int sheetDefId,
        int periodKey,
        bool approved,
        string? reason,
        int userId,
        long? sheetCopyId,
        AccessProfile profile,
        CancellationToken ct)
    {
        var key = new PeriodKey(periodKey);
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
                .CurrentApprovalStepOfAsync(documentId, sheetDefId, key, sheetCopyId, ct)
                .ConfigureAwait(false);

            // ✎ D-442: знімок даних — заново при погодженні, якщо погоджувач правив поданий аркуш (зріз
            // подання його правок не містить). ДО зміни стану: зріз не зберігся — аркуш не погоджено.
            // Автору на ознайомлення аркуш не повертається.
            if (snapshotRefresh is not null)
            {
                await snapshotRefresh.RefreshAsync(documentId, sheetDefId, key, userId, now, ct, sheetCopyId).ConfigureAwait(false);

                // ✎ C-1 (D-475; PO 12.11, дослівно: «якщо є помилки то затвердити теж не можна»): помилка формули
                // блокує й затвердження — той самий код і форма тіла, що в поданні (`ECR-SUB-4221`), свій messageKey.
                // Відхилення (approved = false) не блокується: повернути аркуш на виправлення можна завжди.
                var errors = await snapshotRefresh
                    .FormulaErrorMessagesAsync(sheetDefId, key, currentUser.Language, ct)
                    .ConfigureAwait(false);
                if (errors.Count > 0)
                {
                    await ThrowFormulaErrorsAsync(errors, profile, documentId, key, ct).ConfigureAwait(false);
                }
            }

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
        //
        // ⛔ R5-W1 / W1-01: спершу перехід — у БД (та сама транзакція), потім
        // перерахунок. Статус зрізу рахується запитом `AsNoTracking`, і
        // незбережений перехід він не бачить: затвердження останнього аркуша
        // рахувалося як `Submitted`, а відхилення морозило зріз як поданий.
        // Конфлікт `RowVersion` двох погоджувачів тепер — 409 з `UnitOfWork`,
        // а не 500 із сирого `SaveChanges` будівника зрізу.
        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        await reports.RefreshAsync(documentId, key, ct).ConfigureAwait(false);

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
