using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Projects;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Projects;

/// <summary>Архівація проєкту (`D-123`, `A7-25`).</summary>
/// <remarks>
/// ⛔ Для цього обробника не існувало жодного тесту. Додано разом із
/// перевіркою гранта на конкретний проєкт (`Q-179`, аудит фази 2) — без
/// неї глобальне `Project.Manage` давало б право заархівувати БУДЬ-ЯКИЙ
/// проєкт, не лише свій.
/// </remarks>
public sealed class ArchiveProjectTests
{
    private static readonly DateTime Now = new(2026, 2, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public ArchiveProjectTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(9);

        // B81: перелік винятків і позначка Archived — у транзакції; підміна виконує тіло так, як справжня одиниця роботи.
        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        // ⚠ Грант на 10 — дефолтний Id `ProjectBuilder.Project()`.
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Project.Manage")
                .Grant(ResourceKind.Project, 10, GrantLevel.Manage)
                .Build());
    }

    /// <summary>Активний проєкт з усіма періодами закритими.</summary>
    private Project ArrangeClosed(bool leaveOnePeriodOpen = false)
    {
        var project = ProjectBuilder.Project(timeZoneId: "UTC");
        var policy = ProjectBuilder.Policy();

        ProjectBuilder.Attach(
            project,
            Ecr.Domain.Services.PeriodCalendar.Build(project, policy, TimeZoneInfo.Utc, []));

        project.Activate(Now);

        foreach (var period in project.Periods)
        {
            if (leaveOnePeriodOpen && period == project.Periods[0])
            {
                period.TransitionTo(PeriodState.Open, Now);
                continue;
            }

            period.TransitionTo(PeriodState.Open, Now);
            period.TransitionTo(PeriodState.Grace, Now);
            period.TransitionTo(PeriodState.Closed, Now);
        }

        _periods.FindProjectAsync(project.Id, Arg.Any<CancellationToken>()).Returns(project);

        return project;
    }

    private ArchiveProjectHandler Handler() => new(_periods, _access, _user, _uow, _clock);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "D-123")]
    public async Task Проєкт_з_усіма_закритими_періодами_архівується()
    {
        var project = ArrangeClosed();

        await Handler().HandleAsync(project.Id, CancellationToken.None);

        Assert.Equal(ProjectStatus.Archived, project.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "D-123")]
    public async Task Незакритий_період_блокує_архівацію()
    {
        var project = ArrangeClosed(leaveOnePeriodOpen: true);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => Handler().HandleAsync(project.Id, CancellationToken.None));

        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
        Assert.Equal("err.ECR-PRD-0409.openPeriods", error.Details!["messageKey"]);
        Assert.Equal(ProjectStatus.Active, project.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Без_гранта_на_проєкт_архівація_відхиляється()
    {
        // ⛔ Q-179 (аудит фази 2, авторизація).
        var project = ArrangeClosed();
        // ⚠ S17: грант Read — проєкт видимий, бракує рівня Manage (без гранта — 404).
        _access.BuildProfileAsync(9, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = 9 }
                .Permission("Project.Manage")
                .Grant(ResourceKind.Project, project.Id, GrantLevel.Read)
                .Build());

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(project.Id, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", denied.ErrorCode);
        Assert.Equal("err.ECR-AUTH-0403.noProjectManageGrant", denied.Details!["messageKey"]);
        Assert.Equal(ProjectStatus.Active, project.Status);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "F-08")]
    public async Task Перевідкритий_період_після_until_не_блокує_архівацію()
    {
        // Усі закриті; перший перевідкрито до моменту, який уже минув, а
        // годинна задача ще не прогналася — збережений стан `Grace`.
        //
        // ⚠ Годинник — пізніше за природне закриття січня: інакше розрахунок
        // чесно дав би `Grace` уже за датами, а не за `ReopenedUntil`.
        var project = ArrangeClosed();
        var later = new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Utc);
        _clock.UtcNow.Returns(later);

        var reopened = project.Periods[0];
        reopened.Reopen(later.AddMinutes(-1), "correction", later.AddHours(-1));
        Assert.Equal(PeriodState.Grace, reopened.State);

        // ⛔ Мутація: прибрати `AdvanceTo(Effective…)` в обробнику — тут
        // `ECR-PRD-0409` «є незакриті періоди».
        await Handler().HandleAsync(project.Id, CancellationToken.None);

        Assert.Equal(ProjectStatus.Archived, project.Status);

        // І перехід зафіксовано: архівний проєкт задача станів не обробляє.
        Assert.Equal(PeriodState.Closed, reopened.State);
    }

    /// <remarks>
    /// B69 (G1): діючий виняток «місяць відкрито для документа» блокує архівацію — інакше він переживав її й пускав
    /// перерахунок і Recall в архівований проєкт. Червоне без фіксу: прибрати перевірку винятків в
    /// <c>ArchiveProjectHandler</c> — проєкт архівується (Archived). Контроль: без винятків той самий проєкт архівується.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "B69-G1")]
    public async Task Діючий_виняток_документа_блокує_архівацію()
    {
        var project = ArrangeClosed();
        IReadOnlyList<ProjectDocumentReopenItem> active =
        [
            new(1, 501, "DOC-501", 202602, Now.AddDays(1), "виправлення", "Оператор"),
            new(2, 502, "DOC-502", 202601, Now.AddDays(1), "виправлення", null),
        ];
        var reopens = Substitute.For<IDocumentPeriodReopenStore>();
        reopens.ListActiveForProjectAsync(project.Id, Now, Arg.Any<CancellationToken>()).Returns(active);

        var error = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => new ArchiveProjectHandler(_periods, _access, _user, _uow, _clock, reopens)
                .HandleAsync(project.Id, CancellationToken.None));

        Assert.Equal("ECR-PRD-0409", error.ErrorCode);
        Assert.Equal("err.ECR-PRD-0409.openDocumentReopens", error.Details!["messageKey"]);
        Assert.Equal("202601, 202602", error.Details!["periods"]);
        Assert.Equal(ProjectStatus.Active, project.Status);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());

        // Контроль: винятків немає — той самий проєкт архівується.
        IReadOnlyList<ProjectDocumentReopenItem> empty = [];
        var none = Substitute.For<IDocumentPeriodReopenStore>();
        none.ListActiveForProjectAsync(project.Id, Now, Arg.Any<CancellationToken>()).Returns(empty);
        await new ArchiveProjectHandler(_periods, _access, _user, _uow, _clock, none)
            .HandleAsync(project.Id, CancellationToken.None);

        Assert.Equal(ProjectStatus.Archived, project.Status);
    }

    /// <remarks>
    /// B81 (G3, F13 #877): перелік діючих винятків і позначка Archived — в одній транзакції ПІСЛЯ UPDLOCK кожного
    /// рядка періоду проєкту (той самий замок, під яким `OpenDocumentPeriodReopenHandler` перечитує стан). Без замка
    /// перелік, знятий до коміту паралельного відкриття, пропускав виняток. Детерміновано: фіксується порядок дій
    /// усередині тіла транзакції. Червоне без фіксу: замків немає, перелік і збереження — поза транзакцією.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "B81-G3")]
    public async Task Перелік_винятків_і_архівація_під_замком_усіх_періодів_у_транзакції()
    {
        var project = ArrangeClosed();
        var steps = new List<string>();
        var inTransaction = false;

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                inTransaction = true;
                try
                {
                    await call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1));
                }
                finally
                {
                    inTransaction = false;
                }
            });
        _periods.LockAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                steps.Add($"lock:{call.ArgAt<int>(0)}:{inTransaction}");
                return Task.FromResult<Period?>(null);
            });

        IReadOnlyList<ProjectDocumentReopenItem> empty = [];
        var reopens = Substitute.For<IDocumentPeriodReopenStore>();
        reopens.ListActiveForProjectAsync(project.Id, Now, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                steps.Add($"list:{inTransaction}");
                return Task.FromResult(empty);
            });
        _uow.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                steps.Add($"save:{inTransaction}");
                return Task.FromResult(1);
            });

        await new ArchiveProjectHandler(_periods, _access, _user, _uow, _clock, reopens)
            .HandleAsync(project.Id, CancellationToken.None);

        var expected = project.Periods.OrderBy(p => p.Id).Select(p => $"lock:{p.Id}:True").ToList();
        expected.Add("list:True");
        expected.Add("save:True");
        Assert.Equal(expected, steps);
        Assert.NotEmpty(project.Periods);
        Assert.Equal(ProjectStatus.Archived, project.Status);
    }
}
