// tests/Ecr.Application.Tests/Periods/ReopenArchivedProjectTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Periods;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// ФВ-1.10: у заархівованому проєкті періоди не відкриваються — відмова
/// <c>ECR-PRD-0409</c> з ключем <c>projectArchived</c> (а не сусідніми
/// <c>projectArchivedCurrentPeriod</c>/<c>NoDocuments</c> з API-тестів).
/// </summary>
/// <remarks>
/// Мутаційний доказ (прогнано): у <c>ReopenPeriodHandler</c> прибрати гілку
/// <c>project.Status == ProjectStatus.Archived</c> → тест червоний (період
/// відкривається, виняток не кидається).
/// </remarks>
public sealed class ReopenArchivedProjectTests
{
    private const int UserId = 9;
    private static readonly DateTime Now = new(2026, 4, 1, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Відкриття_періоду_заархівованого_проєкту_відхиляється_з_projectArchived()
    {
        var project = ProjectBuilder.Project(timeZoneId: "UTC");
        project.Activate(Now);
        project.Archive(Now);

        var period = new Period(
            project.Id, new PeriodKey(202601), 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));

        var periods = Substitute.For<IPeriodStore>();
        periods.LockAsync(period.Id, Arg.Any<CancellationToken>()).Returns(period);
        periods.FindProjectAsync(period.ProjectId, Arg.Any<CancellationToken>()).Returns(project);

        var uow = Substitute.For<IUnitOfWork>();
        uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = UserId }
                .Permission(ReopenPeriodHandler.Permission)
                .Grant(ResourceKind.Project, project.Id, GrantLevel.Manage)
                .Build());

        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);

        var audit = Substitute.For<IAuditWriter>();
        var handler = new ReopenPeriodHandler(periods, access, uow, audit, user, new TestClock(Now));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => handler.HandleAsync(period.Id, "уточнення", Now.AddDays(3), CancellationToken.None));

        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
        Assert.Equal("err.ECR-PRD-0409.projectArchived", error.Details!["messageKey"]);
        Assert.Equal(project.Code, error.Details["projectCode"]);

        // Відмова до зміни: аудиту немає, збереження не було.
        await uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.NotEqual(PeriodState.Grace, period.State);
    }
}
