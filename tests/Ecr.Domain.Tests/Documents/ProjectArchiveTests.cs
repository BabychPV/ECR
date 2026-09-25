// tests/Ecr.Domain.Tests/Documents/ProjectArchiveTests.cs
using System.Reflection;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Архівований проєкт — кінцевий стан: повторна архівація й зміна поточного
/// періоду — <c>DomainException ECR-PRD-0409</c> з ключем, а не
/// <c>InvalidOperationException</c> (→ <c>500</c>) чи мовчазний успіх (F-12).
/// </summary>
public sealed class ProjectArchiveTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "F-12")]
    public void Повторна_архівація_дає_конфлікт_стану_з_ключем()
    {
        var project = Archived(out _);

        var error = Assert.Throws<DomainException>(() => project.Archive(Now));

        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
        Assert.Equal("err.ECR-PRD-0409.projectAlreadyArchived", error.Details!["messageKey"]);
        Assert.Equal("PLANT_A", error.Details["projectCode"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "F-12")]
    public void Архівація_чернетки_дає_конфлікт_стану_а_не_InvalidOperationException()
    {
        var project = Make(out _);

        var error = Assert.Throws<DomainException>(() => project.Archive(Now));

        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
        Assert.Equal("err.ECR-PRD-0409.archiveNotActive", error.Details!["messageKey"]);
        Assert.Equal(ProjectStatus.Draft, project.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "F-12")]
    public void Поточний_період_архівованого_проєкту_не_фіксується_і_не_знімається()
    {
        var project = Archived(out var period);

        var pin = Assert.Throws<DomainException>(() => project.PinCurrentPeriod(period.Id, "причина", 7, Now));
        var unpin = Assert.Throws<DomainException>(() => project.UnpinCurrentPeriod(7, Now));

        Assert.All([pin, unpin], e =>
        {
            Assert.Equal("ECR-PRD-0409", e.ErrorCode);
            Assert.Equal("err.ECR-PRD-0409.projectArchivedCurrentPeriod", e.Details!["messageKey"]);
        });
        Assert.Equal(CurrentPeriodMode.Auto, project.CurrentPeriodMode);
        Assert.Null(project.CurrentPeriodChangedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Поточний_період_активного_проєкту_фіксується_як_і_раніше()
    {
        var project = Make(out var period);
        project.Activate(Now);

        project.PinCurrentPeriod(period.Id, "причина", 7, Now);

        Assert.Equal(CurrentPeriodMode.Pinned, project.CurrentPeriodMode);
    }

    private static Project Archived(out Period period)
    {
        var project = Make(out period);
        project.Activate(Now);
        project.Archive(Now);
        Assert.Equal(ProjectStatus.Archived, project.Status);
        return project;
    }

    private static Project Make(out Period period)
    {
        var project = new Project(
            EcrCode.Create("PLANT_A"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Plant A" }),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId: 2, PeriodKind.Monthly, periodPolicyId: 1, "Asia/Almaty");

        period = new Period(project.Id, new PeriodKey(202601), 1,
                            new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        typeof(Entity<int>).GetProperty("Id")!.SetValue(period, 55);

        var field = typeof(Project).GetField("_periods", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((List<Period>)field.GetValue(project)!).Add(period);

        return project;
    }
}
