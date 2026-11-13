using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Documents;

/// <summary>
/// B74 (ADR <c>docs/build/ADR-B74-PROJECT-TEMPLATES.md</c>): шаблон, приєднаний до проєкту, і його чинна версія.
/// </summary>
/// <remarks>
/// ⚠ PT01 (P1) лише додає таблицю і тримає її в згоді з <c>Project.TemplateVersionId</c> (подвійний запис): кожен проєкт
/// має рівно один рядок — свій основний шаблон. Читачі переходять на неї в P2, другий шаблон з'являється в P3.
/// Сурогатний <see cref="Entity{TId}.Id"/> — <c>ResourceId</c> для гранту, звуженого до одного шаблону
/// (<c>ResourceKind.ProjectTemplate</c>, P3). Інваріант «версія належить шаблону» тримає складений FK
/// <c>(CurrentTemplateVersionId, TemplateId) → cfg.TemplateVersion(Id, TemplateId)</c>.
/// </remarks>
public sealed class ProjectTemplate : Entity<int>
{
    private ProjectTemplate() { }

    /// <summary>Приєднує шаблон до проєкту.</summary>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="templateId">Шаблон.</param>
    /// <param name="currentTemplateVersionId">Чинна версія шаблону в проєкті.</param>
    public ProjectTemplate(int projectId, int templateId, int currentTemplateVersionId)
    {
        ProjectId = projectId;
        TemplateId = templateId;
        CurrentTemplateVersionId = currentTemplateVersionId;
    }

    /// <summary>Проєкт.</summary>
    public int ProjectId { get; private set; }

    /// <summary>Шаблон (<c>cfg.Template.Id</c>).</summary>
    public int TemplateId { get; private set; }

    /// <summary>Чинна версія — за нею створюються нові періоди й документи цього шаблону.</summary>
    public int CurrentTemplateVersionId { get; private set; }
}
