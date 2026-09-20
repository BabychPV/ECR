using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Workflow;

/// <summary>Чи може поточний користувач відкликати аркуш (<c>BE-31</c>).</summary>
/// <param name="CanRecall">Рішення сервера; клієнт його не відтворює.</param>
public sealed record RecallAvailabilityDto(bool CanRecall);

/// <summary>
/// Відкликання поданого аркуша АВТОРОМ подання, доки жоден крок маршруту не
/// підписано (<c>BE-31</c>, рішення Q15-04): <c>Submitted → Draft</c> з причиною.
/// </summary>
/// <remarks>
/// ⛔ Право — той самий рівень гранта, що для подання (<see cref="GrantLevel.Submit"/>),
/// але НЕ <c>CanSubmitAsync</c>: те рішення відмовляє на поданому аркуші ще до
/// перевірки гранта. Рівень рахує те саме <see cref="EditRules.Effective"/>.
///
/// ⚠ Зріз подання (<c>calc.SubmissionSnapshot</c>) лишається — це факт, що
/// подання було. Заморожений зріз ЗВІТНОСТІ теж не відкочується, як і при
/// <c>Reopen</c> (<c>ReportSnapshotSync.MarkSubmittedAsync</c>, ФВ-9.17); статус
/// незаморожених перераховується, бо він успадковується від даних (<c>D-65</c>).
/// </remarks>
public sealed class RecallSheetHandler(
    IWorkflowStore workflow,
    IDocumentStore documents,
    IAccessDecisionService access,
    Reporting.ReportSnapshotSync reports,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Відкликає аркуш.</summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="sheetDefId">Аркуш.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="reason">Причина; обов'язкова.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="AccessDeniedException">Немає рівня <c>Submit</c> або відкликає не автор подання.</exception>
    /// <exception cref="ConcurrencyConflictException">Аркуш не поданий або крок уже підписано — <c>ECR-DOC-0409</c>.</exception>
    /// <exception cref="DomainException">Причина порожня — <c>ECR-DOC-0422</c>.</exception>
    public async Task HandleAsync(
        long documentId, int sheetDefId, int periodKey, string reason, CancellationToken ct)
    {
        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може відкликати аркуші.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.signInRequired" });

        var key = new PeriodKey(periodKey);

        // ⛔ Та сама перевірка складу, що й у поданні: `GetOrCreateAsync` створює
        // рядок стану для БУДЬ-ЯКОГО ідентифікатора аркуша (`S-17`).
        if (!await documents.HasSheetAsync(documentId, sheetDefId, ct).ConfigureAwait(false))
        {
            throw new NotFoundException(
                "ECR-DOC-0404",
                $"Аркуша {sheetDefId} немає в складі документа {documentId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-DOC-0404.sheetNotInDocument",
                    ["sheetDefId"] = sheetDefId.ToString(CultureInfo.InvariantCulture),
                    ["documentId"] = documentId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var projectId = await documents.FindProjectIdAsync(documentId, ct).ConfigureAwait(false);

        if (!HasSubmitGrant(profile, projectId, sheetDefId))
        {
            throw new AccessDeniedException(
                "ECR-ACCS-0403",
                $"Відкликання аркуша {sheetDefId} відхилено: немає рівня Submit.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-ACCS-0403.recallDenied",
                    ["sheetDefId"] = sheetDefId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var firstStepId = await FirstStepIdAsync(documentId, projectId, ct).ConfigureAwait(false);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            // ⚠ Той самий `UPDLOCK`, що й у `Reopen`: аркуш не має повернутися в
            // `Draft` у періоді, який паралельно закриває `PeriodStateJob`.
            var period = await workflow.LockPeriodAsync(documentId, key, innerCt).ConfigureAwait(false);
            if (period.State == PeriodState.Closed)
            {
                throw new BusinessRuleException(
                    "ECR-PRD-4223",
                    $"Період {periodKey} закрито: спершу відкрийте період, потім аркуш.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-PRD-4223.reopenPeriodFirst",
                        ["periodKey"] = periodKey.ToString(CultureInfo.InvariantCulture),
                        ["periodState"] = period.State.ToString(),
                    });
            }

            var state = await workflow.GetOrCreateAsync(documentId, sheetDefId, key, innerCt).ConfigureAwait(false);

            // ⛔ Відкликає ЛИШЕ той, хто подав. Колега з тим самим грантом має
            // для цього `Reopen` — окреме право з окремим слідом. На неподаному
            // аркуші автора немає — там відмовляє домен (`409`).
            if (state.Status == DocumentStatus.Submitted && state.SubmittedByUserId != userId)
            {
                throw new AccessDeniedException(
                    "ECR-ACCS-0403", "Відкликати подання може лише той, хто його подав.",
                    new Dictionary<string, object?> { ["messageKey"] = "err.ECR-ACCS-0403.recallNotAuthor" });
            }

            var fromStatus = state.Status;
            var now = clock.UtcNow;

            // ⚠ Саме `ConcurrencyConflictException` (як `ECR-UOM-0409`): статус
            // відповіді береться з ТИПУ, а `DomainException` із того самого правила
            // в `ApprovalState.Recall` доїхав би як 422. Контракт §7 каже 409:
            // погоджувач устиг раніше — це конфлікт стану, а не невірні дані.
            if (!state.IsRecallable(firstStepId))
            {
                throw new ConcurrencyConflictException(
                    "ECR-DOC-0409",
                    $"Відкликати аркуш {sheetDefId} не можна: стан — {state.Status}, або погодження вже почалося.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = state.Status == DocumentStatus.Submitted
                            ? "err.ECR-DOC-0409.recallStepSigned"
                            : "err.ECR-DOC-0409.recallWrongState",
                        ["status"] = state.Status.ToString(),
                    });
            }

            state.Recall(reason, firstStepId);

            await workflow.AddEventAsync(
                ApprovalEvent.For(state, fromStatus, ApprovalAction.Recall, userId, now, reason),
                innerCt).ConfigureAwait(false);

            await reports.RefreshAsync(documentId, key, innerCt).ConfigureAwait(false);
            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Чи пропустив би <see cref="HandleAsync"/> цього користувача саме зараз.</summary>
    /// <remarks>
    /// ⚠ Відповідає <c>false</c>, а не відмовою: це підказка для кнопки. Стан
    /// періоду сюди не входить — закритий період пояснює сама відмова дії.
    /// </remarks>
    public async Task<RecallAvailabilityDto> CanRecallAsync(
        long documentId, int sheetDefId, int periodKey, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
        {
            return new RecallAvailabilityDto(false);
        }

        var key = PeriodKey.Parse(periodKey);
        var profile = await access.BuildProfileAsync(userId, ct).ConfigureAwait(false);
        var projectId = await documents.FindProjectIdAsync(documentId, ct).ConfigureAwait(false);

        if (!HasSubmitGrant(profile, projectId, sheetDefId))
        {
            return new RecallAvailabilityDto(false);
        }

        var sheets = await workflow.GetSheetsAsync(documentId, key, ct).ConfigureAwait(false);
        var state = sheets.FirstOrDefault(s => s.SheetDefId == sheetDefId);

        if (state is null || state.SubmittedByUserId != userId)
        {
            return new RecallAvailabilityDto(false);
        }

        var firstStepId = await FirstStepIdAsync(documentId, projectId, ct).ConfigureAwait(false);

        return new RecallAvailabilityDto(state.IsRecallable(firstStepId));
    }

    private static bool HasSubmitGrant(AccessProfile profile, int? projectId, int sheetDefId)
        => !profile.IsSimulation
           && projectId is { } project
           && EditRules.Effective(profile, new CellAccessContext { ProjectId = project, SheetDefId = sheetDefId })
           >= GrantLevel.Submit;

    /// <summary>Перший крок ЧИННОГО маршруту — той самий вибір, що в <c>CurrentApprovalStepAsync</c>.</summary>
    private async Task<int?> FirstStepIdAsync(long documentId, int? projectId, CancellationToken ct)
    {
        if (projectId is not { } project)
        {
            return null;
        }

        var templateVersionId = await documents.GetTemplateVersionIdAsync(documentId, ct).ConfigureAwait(false);
        var route = await workflow.FindRouteAsync(project, templateVersionId, ct).ConfigureAwait(false);

        return route?.FirstStep?.Id;
    }
}
