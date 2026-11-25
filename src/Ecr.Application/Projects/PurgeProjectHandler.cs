using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Projects;

/// <summary>Попередній перегляд видалення проєкту з даними — що зникне і що заважає.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="Code">Код (людина набирає його для підтвердження).</param>
/// <param name="Status">Стан.</param>
/// <param name="Documents">Документи з ключами й кількістю комірок.</param>
/// <param name="Counts">Кількості рядків.</param>
/// <param name="DataMonths">Місяці з даними (<c>yyyyMM</c>).</param>
/// <param name="ArchivedPeriodKeys">Заархівовані періоди (B69) — видалення заборонене.</param>
/// <param name="Dependants">Документи інших проєктів, що читають цей (B80) — видалення заборонене.</param>
/// <param name="Blocker">
/// Чому видалити не можна: <c>null</c> — можна; інакше ключ каталогу причини (той самий, що у відмові 409).
/// </param>
public sealed record ProjectPurgePreviewDto(
    int ProjectId,
    string Code,
    string Status,
    IReadOnlyList<ProjectPurgeDocument> Documents,
    ProjectPurgeCounts Counts,
    IReadOnlyList<int> DataMonths,
    IReadOnlyList<int> ArchivedPeriodKeys,
    IReadOnlyList<ProjectPurgeDependant> Dependants,
    string? Blocker);

/// <summary>Тіло запиту видалення проєкту з даними.</summary>
/// <param name="Code">Код проєкту, набраний людиною (має збігтися точно).</param>
/// <param name="Reason">Причина (обов'язкова; іде в журнал безпеки).</param>
public sealed record PurgeProjectRequest(string? Code, string? Reason);

/// <summary>
/// ✎ 25.11: видалення НЕ-чернетки проєкту з усіма даними (людина: видалити активні HSE401_2026, TEST_COORD_2026,
/// HSE400_GOLD через інтерфейс; доти Lifecycle → «Delete draft project…» пускав лише порожню чернетку).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Право — НОВЕ <see cref="Permission"/> (<c>Project.DeleteWithData</c>), глобальне (<c>PermissionScopes.Global</c>)
/// і НЕ небезпечне за прапорцем: з вбудованих ролей його бере лише <c>SystemAdministrator</c> шаблоном <c>%</c>
/// (прецедент — <c>Calculation.RecalculateApprovedDependents</c>, B80 N5). Чому не <c>Project.Manage</c>: його мають
/// <c>PeriodAdministrator</c> і ролі з областю проєкту — незворотне стирання чужої роботи не має йти з правом «керувати
/// календарем». Чому не прапорець «небезпечне»: такі права сід не дає ЖОДНІЙ вбудованій ролі, тобто адміністратор не
/// мав би права без окремої ручної дії, а людина просила саме «SystemAdministrator». Глобальне — бо роль з областю
/// («адміністратор лише проєкту A») не має знищувати проєкт цілком.
/// </para>
/// <para>
/// ⛔ Відмови (409 <c>ECR-PRD-0409</c>, ключ причини): архівований проєкт, архівація, що триває, або період, дані якого
/// в архіві (<c>purgeArchived</c>, B69 «архів незмінний»); документ іншого проєкту читає цей (<c>purgeHasDependants</c>,
/// B80: Land читає затверджені місяці HSE400; мапа подій джерела). Перелік залежних — у подробицях відмови й у
/// попередньому перегляді.
/// </para>
/// <para>
/// ⚠ Підтвердження — код проєкту, набраний точно, і обов'язкова причина (422 <c>ECR-PRD-0422</c>). Усе в ОДНІЙ
/// транзакції: структура кожного документа виключно (той самий замок, що й у видаленні документа), факти під
/// <c>UPDLOCK</c>, видалення, запис журналу безпеки з причиною й підсумком, журнал змін, рядок проєкту.
/// </para>
/// </remarks>
public sealed class PurgeProjectHandler(
    IProjectPurgeStore purge,
    ISheetEditGate structureGate,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Право.</summary>
    public const string Permission = "Project.DeleteWithData";

    /// <summary>Подія журналу безпеки.</summary>
    public const string DeletedEventType = "ProjectDeletedWithData";

    /// <summary>Найдовша причина.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Ключ відмови «архів».</summary>
    public const string ArchivedKey = "err.ECR-PRD-0409.purgeArchived";

    /// <summary>Ключ відмови «є залежні».</summary>
    public const string DependantsKey = "err.ECR-PRD-0409.purgeHasDependants";

    /// <summary>Попередній перегляд.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="AccessDeniedException">Немає права.</exception>
    /// <exception cref="NotFoundException">Проєкту немає або він невидимий.</exception>
    public async Task<ProjectPurgePreviewDto> PreviewAsync(int projectId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        ProjectVisibility.RequireVisible(profile, projectId);

        var facts = await purge.ReadFactsAsync(projectId, lockForDelete: false, ct).ConfigureAwait(false)
                    ?? throw ProjectVisibility.NotFound(projectId);

        return new ProjectPurgePreviewDto(
            projectId,
            facts.Code,
            facts.Status.ToString(),
            facts.Documents,
            facts.Counts,
            facts.DataMonths,
            facts.ArchivedPeriodKeys,
            facts.Dependants,
            BlockerKey(facts));
    }

    /// <summary>Видаляє проєкт з усіма даними.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="request">Код і причина.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="AccessDeniedException">Немає права.</exception>
    /// <exception cref="NotFoundException">Проєкту немає або він невидимий.</exception>
    /// <exception cref="DomainException">422: код не збігся або причини немає.</exception>
    /// <exception cref="ConcurrencyConflictException">409: архів або залежні.</exception>
    public async Task HandleAsync(int projectId, PurgeProjectRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var profile = await PermissionCheck.RequireAsync(access, currentUser, Permission, ct).ConfigureAwait(false);
        ProjectVisibility.RequireVisible(profile, projectId);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може видаляти проєкт.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        var reason = request.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0 || reason.Length > MaxReasonLength)
        {
            throw new DomainException(
                "ECR-PRD-0422",
                reason.Length == 0 ? "Причина видалення проєкту обов'язкова." : $"Причина довша за {MaxReasonLength} символів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0422.purgeReasonRequired",
                    ["maxLength"] = MaxReasonLength.ToString(CultureInfo.InvariantCulture),
                });
        }

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var facts = await purge.ReadFactsAsync(projectId, lockForDelete: true, innerCt).ConfigureAwait(false)
                        ?? throw ProjectVisibility.NotFound(projectId);

            // ⛔ Код — точний збіг (з урахуванням регістру): саме він стоїть у заголовку діалогу.
            if (!string.Equals(request.Code?.Trim(), facts.Code, StringComparison.Ordinal))
            {
                throw new DomainException(
                    "ECR-PRD-0422",
                    $"Набраний код не збігається з кодом проєкту {facts.Code}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-PRD-0422.purgeCodeMismatch",
                        ["code"] = facts.Code,
                    });
            }

            Refuse(projectId, facts);

            // ⛔ Структура кожного документа — ВИНЯТКОВО, як у видаленні документа (X8-05): запис, що вже в транзакції,
            // або завершується до видалення (і зникає разом із рештою), або чекає й бачить 404.
            foreach (var document in facts.Documents.OrderBy(d => d.DocumentId))
            {
                await structureGate.EnterStructureAsync(document.DocumentId, exclusive: true, innerCt).ConfigureAwait(false);
            }

            var documentIds = facts.Documents.Select(d => d.DocumentId).Order().ToList();
            var cells = await purge.PurgeAsync(projectId, documentIds, innerCt).ConfigureAwait(false);

            // ⛔ Журнал — у тій самій транзакції (F2-03): збій між видаленням і записом не лишає стирання без сліду.
            await audit.WriteSecurityEventAsync(
                new SecurityEventRecord(
                    clock.UtcNow,
                    DeletedEventType,
                    TargetUserId: null,
                    TargetRoleId: null,
                    JsonSerializer.Serialize(new
                    {
                        projectId,
                        code = facts.Code,
                        status = facts.Status.ToString(),
                        reason,
                        cells,
                        counts = facts.Counts,
                        months = facts.DataMonths,
                        documents = facts.Documents.Select(d => new { d.DocumentId, d.BusinessKey, d.TemplateCode, d.Cells }),
                    }),
                    userId,
                    currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow,
                    TemplateVersionId: facts.TemplateVersionId,
                    EntityType: DeleteProjectHandler.AuditEntityType,
                    EntityId: projectId,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: DeleteProjectHandler.AuditOperation,
                    OldJson: JsonSerializer.Serialize(new { code = facts.Code, status = facts.Status.ToString(), documents = documentIds.Count }),
                    NewJson: null,
                    ChangeReason: reason,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Ключ причини, чому видалити не можна; <c>null</c> — можна.</summary>
    /// <param name="facts">Факти.</param>
    public static string? BlockerKey(ProjectPurgeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Status == ProjectStatus.Archived || facts.IsArchiving || facts.ArchivedPeriodKeys.Count > 0)
        {
            return ArchivedKey;
        }

        return facts.Dependants.Count > 0 ? DependantsKey : null;
    }

    /// <summary>Відмова 409, якщо видалити не можна; подробиці називають архівні періоди чи залежні документи.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="facts">Факти.</param>
    public static void Refuse(int projectId, ProjectPurgeFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var key = BlockerKey(facts);
        if (key is null)
        {
            return;
        }

        var id = projectId.ToString(CultureInfo.InvariantCulture);
        if (key == ArchivedKey)
        {
            throw new ConcurrencyConflictException(
                "ECR-PRD-0409",
                $"Проєкт {facts.Code}: дані в архіві (B69) — архів незмінний, видалення заборонене.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = ArchivedKey,
                    ["projectId"] = id,
                    ["code"] = facts.Code,
                    ["status"] = facts.Status.ToString(),
                    ["periods"] = string.Join(", ", facts.ArchivedPeriodKeys.Select(k => k.ToString(CultureInfo.InvariantCulture))),
                });
        }

        throw new ConcurrencyConflictException(
            "ECR-PRD-0409",
            $"Проєкт {facts.Code} читають документи інших проєктів ({facts.Dependants.Count}): спершу приберіть залежність.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = DependantsKey,
                ["projectId"] = id,
                ["code"] = facts.Code,
                ["count"] = facts.Dependants.Count.ToString(CultureInfo.InvariantCulture),
                ["dependants"] = string.Join(
                    ", ",
                    facts.Dependants.Select(d => $"{d.ProjectCode}/{d.BusinessKey}")),
            });
    }
}
