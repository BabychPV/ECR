using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;

namespace Ecr.Application.Projects;

/// <summary>
/// Видалення проєкту (UAT 08.11, сценарій c): лише ЧЕРНЕТКА без документів і без історії. Право
/// <c>Project.Manage</c> і грант <c>Manage</c> на проєкт; запис у журналі змін.
/// </summary>
/// <remarks>
/// ⛔ Слово людини 08.11: «так додай цей функціонал і він має бути зрозумілий для користувача я про умови».
/// Умови — у відмовах, кожна своїм ключем каталогу з числами (стан, скільки документів), щоб інтерфейс назвав
/// причину словами, а не кодом: активний чи архівний проєкт не видаляється (його дані — чиїсь зобов'язання),
/// чернетка з документами — теж (документи — робота людей), чернетка з історією (прогони розрахунків, зрізи,
/// архівації) — теж.
/// <para>
/// ⚠ Порядок перевірок — як у архівації: видимість (S17, невидимий = неіснуючий) → право й грант → стан.
/// </para>
/// </remarks>
public sealed class DeleteProjectHandler(
    IProjectStore projects,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>Право на видалення — те саме, що на інший життєвий цикл проєкту.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Тип сутності в <c>aud.StructureChange</c>.</summary>
    public const string AuditEntityType = "Project";

    /// <summary>Операція в <c>aud.StructureChange</c>.</summary>
    public const string AuditOperation = "Delete";

    /// <summary>Видаляє проєкт.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Скасування.</param>
    /// <exception cref="NotFoundException">Проєкту немає або він невидимий.</exception>
    /// <exception cref="AccessDeniedException">Немає права чи гранта <c>Manage</c>.</exception>
    /// <exception cref="ConcurrencyConflictException">
    /// <c>ECR-PRD-0409</c>: <c>deleteNotDraft</c> (стан і кількість документів), <c>deleteHasDocuments</c>,
    /// <c>deleteHasHistory</c>.
    /// </exception>
    public async Task HandleAsync(int projectId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);

        // ⛔ S17: невидимий проєкт — як неіснуючий.
        ProjectVisibility.RequireVisible(profile, projectId);

        var userId = currentUser.UserId
                     ?? throw new AccessDeniedException(
                         "ECR-AUTH-0401", "Анонімний запит не може видаляти проєкт.",
                         new Dictionary<string, object?> { ["messageKey"] = "err.ECR-AUTH-0401.anonymousWrite" });

        // ⛔ ФВ-6.14: право — у ЦЬОМУ проєкті; Q-179: грант Manage на конкретний проєкт.
        PermissionCheck.RequireIn(profile, Permission, projectId);

        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Manage)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта Manage на проєкт {projectId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectManageGrant",
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var facts = await projects.GetDeletionFactsAsync(projectId, innerCt).ConfigureAwait(false)
                        ?? throw ProjectVisibility.NotFound(projectId);

            Refuse(projectId, facts);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow,
                    TemplateVersionId: facts.TemplateVersionId,
                    EntityType: AuditEntityType,
                    EntityId: projectId,
                    ChangeClass: ChangeClass.Guarded,
                    Operation: AuditOperation,
                    OldJson: JsonSerializer.Serialize(new { code = facts.Code, status = facts.Status.ToString() }),
                    NewJson: null,
                    ChangeReason: null,
                    ChangedByUserId: userId,
                    CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);

            await projects.DeleteAsync(projectId, innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Відмова, якщо проєкт не порожня чернетка; ключ каталогу називає умову й числа.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="facts">Факти.</param>
    public static void Refuse(int projectId, ProjectDeletionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var id = projectId.ToString(CultureInfo.InvariantCulture);
        var documents = facts.DocumentCount.ToString(CultureInfo.InvariantCulture);

        if (facts.Status != ProjectStatus.Draft)
        {
            throw new ConcurrencyConflictException(
                "ECR-PRD-0409",
                $"Проєкт {projectId} у стані {facts.Status}: видалити можна лише чернетку без документів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0409.deleteNotDraft",
                    ["projectId"] = id,
                    ["status"] = facts.Status.ToString(),
                    ["documentCount"] = documents,
                });
        }

        if (facts.DocumentCount > 0)
        {
            throw new ConcurrencyConflictException(
                "ECR-PRD-0409",
                $"Проєкт {projectId} має документи ({facts.DocumentCount}): видалити можна лише чернетку без документів.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0409.deleteHasDocuments",
                    ["projectId"] = id,
                    ["documentCount"] = documents,
                });
        }

        if (facts.HistoryCount > 0)
        {
            throw new ConcurrencyConflictException(
                "ECR-PRD-0409",
                $"Проєкт {projectId} має історію (розрахунки, зрізи, архівації: {facts.HistoryCount}): видалення її знищило б.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0409.deleteHasHistory",
                    ["projectId"] = id,
                    ["historyCount"] = facts.HistoryCount.ToString(CultureInfo.InvariantCulture),
                });
        }
    }
}
