// tests/Ecr.Domain.Tests/Requirements/ProjectLifecycleRequirementTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Requirements;

/// <summary>
/// ФВ-1.4: життєвий цикл проєкту доходить до <c>Archived</c> і лише вперед.
/// </summary>
/// <remarks>
/// ⚠ Текст ФВ-1.4 називає чотири стани (<c>Draft → Active → Closed → Archived</c>),
/// а рішення <c>D-123</c> (<c>docs/tz/10-decisions.md</c>) прибрало <c>Closed</c> на
/// рівні проєкту: він має сенс лише для періоду. Тест фіксує чинний ланцюг
/// <c>Draft → Active → Archived</c>; розбіжність тексту вимоги з <c>D-123</c> —
/// відкрите питання до документа, а не до коду.
///
/// Мутаційний доказ: у <c>Project.Archive</c> замінити
/// <c>Status = ProjectStatus.Archived</c> на <c>Status = ProjectStatus.Active</c> —
/// тест почервоніє на першому ж <c>Assert.Equal(Archived, …)</c>.
/// </remarks>
public sealed class ProjectLifecycleRequirementTests
{
    private static readonly DateTime Now = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-1.4")]
    public void Проєкт_проходить_Draft_Active_Archived_і_назад_не_повертається()
    {
        var project = new Project(
            EcrCode.Create("LIFE_2026"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Lifecycle" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: 1, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");

        // Стани переліку — рівно ті, до яких веде перехід (D-123).
        Assert.Equal(
            new[] { ProjectStatus.Draft, ProjectStatus.Active, ProjectStatus.Archived },
            Enum.GetValues<ProjectStatus>());

        Assert.Equal(ProjectStatus.Draft, project.Status);

        // Чернетку не можна одразу заархівувати: цикл не перестрибується.
        var skip = Assert.Throws<DomainException>(() => project.Archive(Now));
        Assert.Equal("err.ECR-PRD-0409.archiveNotActive", skip.Details!["messageKey"]);
        Assert.Equal(ProjectStatus.Draft, project.Status);

        project.Activate(Now);
        Assert.Equal(ProjectStatus.Active, project.Status);

        project.Archive(Now.AddDays(1));
        Assert.Equal(ProjectStatus.Archived, project.Status);
        Assert.Equal(Now.AddDays(1), project.ClosedAt);

        // Archived — кінцевий: ні повторної активації, ні повторної архівації.
        Assert.Throws<InvalidOperationException>(() => project.Activate(Now.AddDays(2)));
        var again = Assert.Throws<DomainException>(() => project.Archive(Now.AddDays(2)));
        Assert.Equal("err.ECR-PRD-0409.projectAlreadyArchived", again.Details!["messageKey"]);
        Assert.Equal(ProjectStatus.Archived, project.Status);
    }
}
