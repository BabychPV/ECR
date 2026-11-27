using System.Globalization;
using System.Text.Json;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Projects;

/// <summary>B74 P3 (D-576): шаблон проєкту для вкладки «Templates» і кроку «Template» майстра документа.</summary>
/// <param name="ProjectTemplateId">Рядок <c>doc.ProjectTemplate</c>.</param>
/// <param name="TemplateId">Шаблон.</param>
/// <param name="TemplateCode">Код шаблону (префікс бізнес-ключа в багатошаблонному проєкті).</param>
/// <param name="TemplateNameL10n">Назва шаблону.</param>
/// <param name="CurrentTemplateVersionId">Чинна версія шаблону в проєкті.</param>
/// <param name="Version">Позначення чинної версії.</param>
/// <param name="Layout">Розкладка чинної версії.</param>
/// <param name="IsPrimary">Основний шаблон проєкту: не від'єднується до P6.</param>
/// <param name="DocumentCount">Документів цього шаблону в проєкті; &gt; 0 — від'єднання відмовить.</param>
/// <param name="SingleDocumentPerProject">
/// ✎ B74 (24.11): шаблон дозволяє щонайбільше один документ у проєкті (B151); з <c>DocumentCount</c> &gt; 0 майстер
/// нового документа замість Next пропонує відкрити наявний — створення однаково дало б <c>409 ECR-DOC-4092</c>.
/// </param>
public sealed record ProjectTemplateDto(
    int ProjectTemplateId,
    int TemplateId,
    string TemplateCode,
    LocalizedText TemplateNameL10n,
    int CurrentTemplateVersionId,
    string Version,
    string Layout,
    bool IsPrimary,
    int DocumentCount,
    bool SingleDocumentPerProject)
{
    internal static ProjectTemplateDto From(ProjectTemplateRow row) => new(
        row.ProjectTemplateId, row.TemplateId, row.TemplateCode, row.TemplateNameL10n, row.CurrentTemplateVersionId,
        row.Version, row.Layout.ToString(), row.IsPrimary, row.DocumentCount, row.SingleDocumentPerProject);
}

/// <summary>
/// Шаблони проєкту (<c>GET /projects/{id}/templates</c>). Видимість — як шкала версій: право <c>Document.View</c> у
/// проєкті й видимий проєкт; невидимий — той самий <c>404</c>, що й неіснуючий (S17).
/// </summary>
public sealed class ListProjectTemplatesHandler(
    IProjectTemplateStore store, IAccessDecisionService access, ICurrentUser currentUser)
{
    /// <summary>Право перегляду.</summary>
    public const string Permission = "Document.View";

    /// <summary>Перелік.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<IReadOnlyList<ProjectTemplateDto>> HandleAsync(int projectId, CancellationToken ct)
    {
        var profile = await PermissionCheck.RequireInAnyProjectAsync(access, currentUser, Permission, ct)
            .ConfigureAwait(false);
        ProjectVisibility.RequireVisible(profile, projectId);
        PermissionCheck.RequireIn(profile, Permission, projectId);

        var rows = await store.ListAsync(projectId, ct).ConfigureAwait(false);
        return [.. rows.Select(ProjectTemplateDto.From)];
    }
}

/// <summary>
/// Приєднує шаблон до проєкту (<c>POST /projects/{id}/templates</c>). Право <c>Project.Manage</c> у проєкті й грант
/// <c>Manage</c> — як маршрут погодження проєкту.
/// </summary>
/// <remarks>
/// ⛔ Під блоком рядка проєкту (той самий, що в створенні документа й переносі версії): перевірки «вже приєднано»,
/// стану проєкту й періодичності бачать зафіксоване. Періодичність одна на проєкт (ADR B74, Q1): річний проєкт не
/// бере версію «копія на місяць» — те саме правило D-474, що при створенні проєкту й переносі версії.
/// </remarks>
public sealed class AttachProjectTemplateHandler(
    IProjectTemplateStore store,
    IRepository<TemplateVersion, int> versions,
    ITemplateVersionStore templates,
    IAuditWriter audit,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право керування проєктом.</summary>
    public const string Permission = "Project.Manage";

    /// <summary>Сутність журналу структурних змін.</summary>
    public const string AuditEntityType = "ProjectTemplate";

    /// <summary>Приєднує.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="templateVersionId">Версія шаблону, з якою він стає в проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ProjectTemplateDto> HandleAsync(int projectId, int templateVersionId, CancellationToken ct)
    {
        var profile = await Workflow.ApprovalRouteScopeAccess
            .RequireProjectManageAsync(access, currentUser, Permission, projectId, ct)
            .ConfigureAwait(false);

        var version = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
                      ?? throw VersionNotFound(templateVersionId);
        var template = await templates.FindTemplateOfVersionAsync(templateVersionId, ct).ConfigureAwait(false)
                       ?? throw VersionNotFound(templateVersionId);

        // ⛔ Архівований шаблон нових документів не приймає (BE-26) — приєднувати його означало б дати крок майстра,
        // що завжди відмовляє.
        template.EnsureOfferedForNewDocuments();

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var facts = await store.LockProjectAsync(projectId, innerCt).ConfigureAwait(false)
                        ?? throw ProjectVisibility.NotFound(projectId);

            EnsureProjectOpen(facts, projectId);

            if (facts.PeriodKind == PeriodKind.Yearly && version.Layout == TemplateLayout.PerMonthCopies)
            {
                throw new BusinessRuleException(
                    ErrorCodes.TemplateInvalid,
                    "Річний проєкт потребує версії шаблону з розкладкою «рік, місяці в колонках».",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-TMPL-0422.yearlyProjectNeedsYearLayout",
                        ["versionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
                    });
            }

            if (await store.FindAsync(projectId, template.Id, innerCt).ConfigureAwait(false) is not null)
            {
                throw new DomainException(
                    "ECR-PRD-0409",
                    $"Шаблон {template.Code} уже є в проєкті {projectId}.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-PRD-0409.templateAlreadyAttached",
                        ["templateCode"] = template.Code,
                    });
            }

            store.Attach(projectId, template.Id, templateVersionId);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, templateVersionId, AuditEntityType, projectId, ChangeClass.Safe, "Attach",
                    OldJson: null,
                    NewJson: JsonSerializer.Serialize(new { projectId, templateId = template.Id, templateVersionId }),
                    ChangeReason: null, ChangedByUserId: profile.UserId, CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        var row = await store.FindAsync(projectId, template.Id, ct).ConfigureAwait(false)
                  ?? throw ProjectVisibility.NotFound(projectId);
        return ProjectTemplateDto.From(row);
    }

    internal static void EnsureProjectOpen(ProjectTemplateLockFacts facts, int projectId)
    {
        if (facts.Status == ProjectStatus.Archived || facts.IsArchiving)
        {
            throw new DomainException(
                "ECR-PRD-0409",
                $"Проєкт {projectId} заархівований: склад його шаблонів не змінюється.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRD-0409.projectArchivedTemplates",
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }
    }

    private static NotFoundException VersionNotFound(int versionId)
        => new(
            ErrorCodes.TemplateNotFound, $"Версії шаблону {versionId} не існує.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                ["versionId"] = versionId.ToString(CultureInfo.InvariantCulture),
            });
}

/// <summary>
/// Від'єднує шаблон від проєкту (<c>DELETE /projects/{id}/templates/{templateId}</c>). Право — як приєднання.
/// </summary>
/// <remarks>
/// ⛔ Відмова (<c>409 ECR-PRD-0409</c>), а не каскад: документи шаблону (<c>templateHasDocuments</c>), основний шаблон
/// (<c>primaryTemplate</c> — до P6 на ньому тримається <c>Project.TemplateVersionId</c>) і власний маршрут погодження
/// пари (<c>templateHasRoute</c>, B155 — конфігурація не губиться мовчки). Усе — під блоком рядка проєкту: документ,
/// що створюється одночасно, або встигає й дає відмову, або бачить від'єднаний шаблон і відмовляє сам.
/// </remarks>
public sealed class DetachProjectTemplateHandler(
    IProjectTemplateStore store,
    IAuditWriter audit,
    IUnitOfWork uow,
    IAccessDecisionService access,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>Право керування проєктом.</summary>
    public const string Permission = AttachProjectTemplateHandler.Permission;

    /// <summary>Від'єднує.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task HandleAsync(int projectId, int templateId, CancellationToken ct)
    {
        var profile = await Workflow.ApprovalRouteScopeAccess
            .RequireProjectManageAsync(access, currentUser, Permission, projectId, ct)
            .ConfigureAwait(false);

        await uow.ExecuteInTransactionAsync(async innerCt =>
        {
            var facts = await store.LockProjectAsync(projectId, innerCt).ConfigureAwait(false)
                        ?? throw ProjectVisibility.NotFound(projectId);

            AttachProjectTemplateHandler.EnsureProjectOpen(facts, projectId);

            var row = await store.FindAsync(projectId, templateId, innerCt).ConfigureAwait(false)
                      ?? throw NotInProject(projectId, templateId);

            if (row.IsPrimary || facts.PrimaryTemplateId == templateId)
            {
                throw Conflict("err.ECR-PRD-0409.primaryTemplate", $"Шаблон {row.TemplateCode} — основний шаблон проєкту {projectId}.", row);
            }

            var documents = await store.CountDocumentsAsync(projectId, templateId, isPrimary: false, innerCt)
                .ConfigureAwait(false);
            if (documents > 0)
            {
                throw Conflict(
                    "err.ECR-PRD-0409.templateHasDocuments",
                    $"У проєкті {projectId} є {documents} документ(ів) шаблону {row.TemplateCode}.", row, documents);
            }

            if (await store.HasOwnRouteAsync(projectId, templateId, innerCt).ConfigureAwait(false))
            {
                throw Conflict(
                    "err.ECR-PRD-0409.templateHasRoute", $"Пара проєкт {projectId} × шаблон {row.TemplateCode} має власний маршрут погодження.", row);
            }

            await store.DetachAsync(projectId, templateId, innerCt).ConfigureAwait(false);

            await audit.WriteStructureChangeAsync(
                new StructureChangeRecord(
                    clock.UtcNow, row.CurrentTemplateVersionId, AttachProjectTemplateHandler.AuditEntityType, projectId,
                    ChangeClass.Safe, "Detach",
                    OldJson: JsonSerializer.Serialize(new { projectId, templateId, templateVersionId = row.CurrentTemplateVersionId }),
                    NewJson: null,
                    ChangeReason: null, ChangedByUserId: profile.UserId, CorrelationId: currentUser.CorrelationId),
                innerCt).ConfigureAwait(false);

            await uow.SaveChangesAsync(innerCt).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Шаблон не приєднано до проєкту — <c>404 ECR-TMPL-0404</c>.</summary>
    internal static NotFoundException NotInProject(int projectId, int templateId)
        => new(
            ErrorCodes.TemplateNotFound, $"Шаблону {templateId} немає в проєкті {projectId}.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-TMPL-0404.templateNotInProject",
                ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                ["templateId"] = templateId.ToString(CultureInfo.InvariantCulture),
            });

    private static DomainException Conflict(string messageKey, string message, ProjectTemplateRow row, int? documents = null)
    {
        var details = new Dictionary<string, object?>
        {
            ["messageKey"] = messageKey,
            ["templateCode"] = row.TemplateCode,
        };

        if (documents is { } count)
        {
            details["documentCount"] = count.ToString(CultureInfo.InvariantCulture);
        }

        return new DomainException("ECR-PRD-0409", message, details);
    }
}
