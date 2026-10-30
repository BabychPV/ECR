using System.Globalization;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Projects;

/// <summary>Рядок часової шкали версій шаблону проєкту (RPT-STABLE Part B).</summary>
/// <param name="FromPeriodKey">Перший період дії версії (<c>Рік*100+№</c>).</param>
/// <param name="TemplateVersionId">Версія шаблону.</param>
/// <param name="Version">Номер версії (як у переліку документів).</param>
/// <param name="TemplateCode">Код шаблону.</param>
public sealed record ProjectTemplateTimelineItemDto(int FromPeriodKey, int TemplateVersionId, string Version, string? TemplateCode);

/// <summary>Часова шкала версій шаблону проєкту: з якого періоду яка версія діє.</summary>
/// <param name="ProjectId">Проєкт.</param>
/// <param name="CurrentVersionId">Чинна (остання) версія — за нею створюються НОВІ періоди.</param>
/// <param name="Items">Рядки шкали за зростанням початку; порожньо — проєкт має одну (чинну) версію на всі періоди.</param>
public sealed record ProjectTemplateTimelineDto(
    int ProjectId, int CurrentVersionId, IReadOnlyList<ProjectTemplateTimelineItemDto> Items);

/// <summary>
/// Читає часову шкалу версій шаблону проєкту (<c>GET /projects/{id}/template-versions</c>). Видимість — як календар
/// періодів проєкту: право <c>Document.View</c> і видимий проєкт (невидимий — той самий <c>404</c>, що й неіснуючий).
/// </summary>
public sealed class GetProjectTemplateTimelineHandler(
    IProjectVersionTimeline timeline,
    IRepository<TemplateVersion, int> versions,
    ITemplateVersionStore templates,
    Security.IAccessDecisionService access,
    Common.ICurrentUser currentUser)
{
    /// <summary>Повертає шкалу проєкту.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="ct">Токен скасування.</param>
    public async Task<ProjectTemplateTimelineDto> HandleAsync(int projectId, CancellationToken ct)
    {
        var profile = await Security.PermissionCheck
            .RequireInAnyProjectAsync(access, currentUser, "Document.View", ct)
            .ConfigureAwait(false);

        var byParticipation = !profile.SeesDocumentsOf(projectId)
                              && profile.HasDocumentParticipations
                              && await access.ParticipatesInProjectAsync(profile, projectId, ct).ConfigureAwait(false);
        if (!byParticipation)
        {
            ProjectVisibility.RequireVisible(profile, projectId);
            Security.PermissionCheck.RequireIn(profile, "Document.View", projectId);
        }

        var current = await timeline.CurrentVersionAsync(projectId, ct).ConfigureAwait(false)
                      ?? throw ProjectVisibility.NotFound(projectId);
        var entries = await timeline.GetAsync(projectId, ct).ConfigureAwait(false);

        var items = new List<ProjectTemplateTimelineItemDto>(entries.Count);
        foreach (var entry in entries)
        {
            var version = await versions.FindAsync(entry.TemplateVersionId, ct).ConfigureAwait(false);
            var template = await templates.FindTemplateOfVersionAsync(entry.TemplateVersionId, ct).ConfigureAwait(false);
            items.Add(new ProjectTemplateTimelineItemDto(
                entry.FromPeriodKey,
                entry.TemplateVersionId,
                version?.Version ?? entry.TemplateVersionId.ToString(CultureInfo.InvariantCulture),
                template?.Code));
        }

        return new ProjectTemplateTimelineDto(projectId, current, items);
    }
}
