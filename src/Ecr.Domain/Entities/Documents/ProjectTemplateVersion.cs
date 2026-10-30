namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// Рядок часової шкали версій шаблону проєкту (RPT-STABLE Part B): з періоду <see cref="FromPeriodKey"/>
/// (включно) і до наступного рядка діє версія <see cref="TemplateVersionId"/>.
/// </summary>
/// <remarks>
/// ⛔ Версію періоду визначає ЛИШЕ <c>IProjectVersionTimeline.VersionOfAsync</c> (і її SQL-відповідник
/// <c>doc.fn_VersionOf</c>): найбільший <see cref="FromPeriodKey"/> ≤ ключа періоду. <c>Project.TemplateVersionId</c>
/// лишається «чинною/останньою» версією — тією, за якою створюються НОВІ періоди.
/// ⚠ Ключ складений (<c>ProjectId</c>, <c>FromPeriodKey</c>): рядок на період, з якого версія змінилась.
/// </remarks>
public sealed class ProjectTemplateVersion
{
    private ProjectTemplateVersion() { }

    /// <summary>Створює рядок шкали.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="fromPeriodKey">Перший період (<c>Рік*100+№</c>), на який діє версія.</param>
    /// <param name="templateVersionId">Опублікована версія шаблону.</param>
    public ProjectTemplateVersion(int projectId, int fromPeriodKey, int templateVersionId)
    {
        ProjectId = projectId;
        FromPeriodKey = fromPeriodKey;
        TemplateVersionId = templateVersionId;
    }

    /// <summary>Проєкт.</summary>
    public int ProjectId { get; private set; }

    /// <summary>Перший період дії версії.</summary>
    public int FromPeriodKey { get; private set; }

    /// <summary>Версія шаблону.</summary>
    public int TemplateVersionId { get; private set; }
}
