// src/Ecr.Application/Documents/GetDocumentTemplateHandler.cs
using System.Globalization;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Templates.Dto;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents;

/// <summary>Аркуш, який можна включити в новий документ.</summary>
/// <param name="Id">Ідентифікатор <c>SheetDef</c> — те, що йде в <c>sheetDefIds</c>.</param>
/// <param name="Code">Код аркуша.</param>
/// <param name="NameL10n">Назва всіма мовами каталогу.</param>
/// <param name="SheetGroup">Група для правил складу; <c>null</c> — поза групами.</param>
/// <param name="IsMandatory">Чи обов'язковий аркуш.</param>
public sealed record DocumentTemplateSheetDto(
    int Id, string Code, LocalizedText NameL10n, string? SheetGroup, bool IsMandatory);

/// <summary>З чого складається новий документ проєкту.</summary>
/// <param name="TemplateVersionId">Версія шаблону проєкту — те, що йде в <c>templateVersionId</c>.</param>
/// <param name="TemplateCode">Код шаблону.</param>
/// <param name="Version">Позначення версії.</param>
/// <param name="Sheets">Аркуші версії в порядку <c>Ordinal</c>.</param>
/// <param name="GroupRules">Правила складу (<c>SheetGroupRule</c>) — для попередження ДО збереження.</param>
public sealed record DocumentTemplateDto(
    int TemplateVersionId,
    string TemplateCode,
    string Version,
    IReadOnlyList<DocumentTemplateSheetDto> Sheets,
    IReadOnlyList<SheetGroupRuleDto> GroupRules);

/// <summary>
/// Версія шаблону й аркуші для створення документа в проєкті. Право
/// <c>Document.Create</c> і грант <c>Write</c> на проєкт — ті самі, що й на
/// <c>POST /documents</c>.
/// </summary>
/// <remarks>
/// ⛔ V-12 (UX-прохід, третій раунд). Діалог «New document» брав версії з
/// <c>GET /templates</c> і структуру з <c>GET /template-versions/{id}/structure</c>
/// — обидва вимагають <c>Template.View</c>. Оператор із правом
/// <c>Document.Create</c> отримував «You do not have permission», хоча сам
/// <c>POST /documents</c> від нього — 201.
///
/// ⚠ Версію визначає ПРОЄКТ (<c>Project.TemplateVersionId</c>), а не вибір
/// людини: документ на іншій версії відкривається без аркушів (V-11). Тому
/// ендпоінт прив'язаний до проєкту, а не до шаблону, і не розширює права
/// оператора на шаблони — він віддає рівно те, без чого документ не створити:
/// перелік аркушів і правила їх складу, без таблиць, колонок і формул.
/// </remarks>
public sealed class GetDocumentTemplateHandler(
    IPeriodStore periods,
    IRepository<TemplateVersion, int> versions,
    ITemplateVersionStore templates,
    IMetadataCache metadata,
    IDocumentStore documents,
    IAccessDecisionService access,
    ICurrentUser currentUser)
{
    /// <summary>Повертає версію шаблону проєкту та її аркуші.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <exception cref="NotFoundException">Проєкту немає або він невидимий.</exception>
    /// <exception cref="AccessDeniedException">Немає <c>Document.Create</c> чи гранта <c>Write</c>.</exception>
    public async Task<DocumentTemplateDto> HandleAsync(int projectId, CancellationToken ct)
    {
        var profile = await PermissionCheck
            .RequireAsync(access, currentUser, CreateDocumentHandler.Permission, ct)
            .ConfigureAwait(false);

        var project = await periods.FindProjectAsync(projectId, ct).ConfigureAwait(false);

        // ⚠ Невидимий проєкт — «не знайдено», як і неіснуючий: різниця між 403
        // і 404 сама була б відомістю про те, що проєкт є.
        if (project is null || profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Read)
        {
            throw new NotFoundException(
                ErrorCodes.ProjectNotFound, $"Проєкту {projectId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-PRJ-0404.project",
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        if (profile.LevelFor(ResourceKind.Project, projectId) < GrantLevel.Write)
        {
            throw new AccessDeniedException(
                "ECR-AUTH-0403", $"Немає гранта на запис у проєкт {projectId}.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-AUTH-0403.noProjectWriteGrant",
                    ["projectId"] = projectId.ToString(CultureInfo.InvariantCulture),
                });
        }

        var templateVersionId = project.TemplateVersionId;

        var version = await versions.FindAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Версії шаблону {templateVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
                });

        var template = await templates.FindTemplateOfVersionAsync(templateVersionId, ct).ConfigureAwait(false)
            ?? throw new NotFoundException(
                ErrorCodes.TemplateNotFound, $"Версії шаблону {templateVersionId} не існує.",
                new Dictionary<string, object?>
                {
                    ["messageKey"] = "err.ECR-TMPL-0404.templateVersion",
                    ["versionId"] = templateVersionId.ToString(CultureInfo.InvariantCulture),
                });

        // ⛔ `V-11`: архівований шаблон нових документів не приймає (`BE-26`,
        // `Template.EnsureOfferedForNewDocuments`), тож і діалогу він не
        // пропонується: людина бачить ту саму відмову `ECR-TMPL-0409`, яку дав би
        // `POST /documents`, — ДО того, як обере аркуші.
        template.EnsureOfferedForNewDocuments();

        var snapshot = await metadata.GetAsync(templateVersionId, ct).ConfigureAwait(false);
        var groupRules = await documents.GetGroupRulesAsync(templateVersionId, ct).ConfigureAwait(false);

        return new DocumentTemplateDto(
            templateVersionId,
            template.Code,
            version.Version,
            [.. snapshot.Sheets
                .OrderBy(s => s.Ordinal)
                .Select(s => new DocumentTemplateSheetDto(s.Id, s.Code, s.NameL10n, s.SheetGroup, s.IsMandatory))],
            [.. groupRules.Select(r => new SheetGroupRuleDto(r.SheetGroup, r.RuleKind, r.TargetGroup))]);
    }
}
