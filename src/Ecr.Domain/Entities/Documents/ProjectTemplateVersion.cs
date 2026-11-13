namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Рядок часової шкали версій шаблону проєкту (RPT-STABLE Part B): з періоду <see cref="FromPeriodKey"/>
/// (включно) і до наступного рядка діє версія <see cref="TemplateVersionId"/>.
/// </summary>
/// <remarks>
/// ⛔ Версію періоду визначає ЛИШЕ <c>IProjectVersionTimeline.VersionOfAsync</c> (і її SQL-відповідник
/// <c>doc.fn_VersionOf</c>): найбільший <see cref="FromPeriodKey"/> ≤ ключа періоду. <c>Project.TemplateVersionId</c>
/// лишається «чинною/останньою» версією — тією, за якою створюються НОВІ періоди.
/// ✎ B74 PT01: ключ складений (<c>ProjectId</c>, <c>TemplateId</c>, <c>FromPeriodKey</c>) — шкала на пару (проєкт, шаблон).
/// <see cref="TemplateVersionId"/> мусить належати <see cref="TemplateId"/>: це тримає складений FK
/// <c>(TemplateVersionId, TemplateId) → cfg.TemplateVersion(Id, TemplateId)</c>.
/// </remarks>
public sealed class ProjectTemplateVersion
{
    private ProjectTemplateVersion() { }

    /// <summary>Створює рядок шкали.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="templateId">Шаблон (<c>cfg.Template.Id</c>), якому належить версія.</param>
    /// <param name="fromPeriodKey">Перший період (<c>Рік*100+№</c>), на який діє версія.</param>
    /// <param name="templateVersionId">Опублікована версія шаблону.</param>
    public ProjectTemplateVersion(int projectId, int templateId, int fromPeriodKey, int templateVersionId)
    {
        ProjectId = projectId;
        TemplateId = templateId;
        FromPeriodKey = fromPeriodKey;
        TemplateVersionId = templateVersionId;
    }

    /// <summary>Проєкт.</summary>
    public int ProjectId { get; private set; }

    /// <summary>Шаблон (B74).</summary>
    public int TemplateId { get; private set; }

    /// <summary>Перший період дії версії.</summary>
    public int FromPeriodKey { get; private set; }

    /// <summary>Версія шаблону.</summary>
    public int TemplateVersionId { get; private set; }
}
