// tests/Ecr.Application.Tests/Periods/ProjectDocumentReopensTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Periods;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Periods;

/// <summary>
/// Приймальна i4: вкладка Periods — «Locked · 1 document reopened until …» з номером документа і «Close now».
/// Перелік винятків проєкту (<see cref="ListProjectDocumentReopensHandler"/>) і закриття до строку
/// (<see cref="CloseDocumentPeriodReopenHandler"/>).
/// </summary>
/// <remarks>
/// Мутації (прогнано, червоне): (1) у переліку не питати <c>CanReadDocumentAsync</c> → схований документ у відповіді;
/// (2) прибрати <c>ProjectVisibility.RequireVisible</c> → невидимий проєкт дає перелік замість 404; (3) у закритті
/// прибрати <c>PermissionCheck.RequireIn</c> → закриття без права проходить; (4) закривати з причиною
/// <c>ClosedReplaced</c> → причина й журнал не «Closed».
/// </remarks>
public sealed class ProjectDocumentReopensTests
{
    private const int UserId = 9;
    private const int ProjectId = 10;
    private const long VisibleDocumentId = 501;
    private const long HiddenDocumentId = 502;
    private const int PeriodKey = 202601;
    private static readonly DateTime Now = new(2026, 2, 10, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Перелік_не_показує_схований_документ_і_каже_чи_можна_закрити()
    {
        var store = Substitute.For<IDocumentPeriodReopenStore>();
        store.ListActiveForProjectAsync(ProjectId, Now, Arg.Any<CancellationToken>())
            .Returns(new List<ProjectDocumentReopenItem>
            {
                new(1, VisibleDocumentId, "DOC-1", PeriodKey, Now.AddHours(5), "Лист №17", "Admin"),
                new(2, HiddenDocumentId, "SECRET-2", PeriodKey, Now.AddHours(6), "Схований", "Admin"),
            });

        var access = Access(withReopen: true);

        var result = await new ListProjectDocumentReopensHandler(store, access, User(), new TestClock(Now))
            .HandleAsync(ProjectId, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(VisibleDocumentId, item.DocumentId);
        Assert.Equal("DOC-1", item.DocumentName);
        Assert.True(result.CanClose);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Без_права_перелік_є_але_закрити_не_можна()
    {
        var store = Substitute.For<IDocumentPeriodReopenStore>();
        store.ListActiveForProjectAsync(ProjectId, Now, Arg.Any<CancellationToken>())
            .Returns(new List<ProjectDocumentReopenItem>
            {
                new(1, VisibleDocumentId, "DOC-1", PeriodKey, Now.AddHours(5), "Лист №17", null),
            });

        var result = await new ListProjectDocumentReopensHandler(store, Access(withReopen: false), User(), new TestClock(Now))
            .HandleAsync(ProjectId, CancellationToken.None);

        Assert.Single(result.Items);
        Assert.False(result.CanClose);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Невидимий_проєкт_404_і_сховище_не_питається()
    {
        var store = Substitute.For<IDocumentPeriodReopenStore>();
        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = UserId }.Permission(ReopenPeriodHandler.Permission).Build());

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => new ListProjectDocumentReopensHandler(store, access, User(), new TestClock(Now))
                .HandleAsync(ProjectId, CancellationToken.None));

        Assert.Equal("ECR-PRJ-0404", error.ErrorCode);
        await store.DidNotReceiveWithAnyArgs().ListActiveForProjectAsync(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Закрити_зараз_закриває_виняток_людиною_і_пише_журнал_Close()
    {
        var exception = DocumentPeriodReopen.Open(VisibleDocumentId, PeriodKey, Now.AddHours(5), "Лист №17", UserId, Now.AddHours(-1));
        var store = Store(exception);
        var audit = Substitute.For<IAuditWriter>();
        var uow = Uow();

        await new CloseDocumentPeriodReopenHandler(store, Access(withReopen: true), uow, audit, User(), new TestClock(Now))
            .HandleAsync(VisibleDocumentId, PeriodKey, CancellationToken.None);

        Assert.Equal(Now, exception.ClosedAt);
        Assert.Equal(UserId, exception.ClosedByUserId);
        Assert.Equal(DocumentPeriodReopen.ClosedManually, exception.CloseReason);
        Assert.False(exception.IsActiveAt(Now));
        await uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.Operation == "Close"
                && r.EntityType == OpenDocumentPeriodReopenHandler.AuditEntityType
                && r.ChangedByUserId == UserId
                && r.ChangeReason == "{\"k\":\"document.reopen.audit.closedManually\"}"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Закрити_без_права_Period_Reopen_403_і_виняток_діє()
    {
        var exception = DocumentPeriodReopen.Open(VisibleDocumentId, PeriodKey, Now.AddHours(5), "Лист №17", UserId, Now.AddHours(-1));
        var store = Store(exception);
        var audit = Substitute.For<IAuditWriter>();

        var error = await Assert.ThrowsAsync<AccessDeniedException>(
            () => new CloseDocumentPeriodReopenHandler(store, Access(withReopen: false), Uow(), audit, User(), new TestClock(Now))
                .HandleAsync(VisibleDocumentId, PeriodKey, CancellationToken.None));

        Assert.Equal("ECR-AUTH-0403", error.ErrorCode);
        Assert.True(exception.IsActiveAt(Now));
        await audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Закрити_схований_документ_404_як_відсутній()
    {
        var exception = DocumentPeriodReopen.Open(HiddenDocumentId, PeriodKey, Now.AddHours(5), "Схований", UserId, Now.AddHours(-1));
        var store = Store(exception);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => new CloseDocumentPeriodReopenHandler(store, Access(withReopen: true), Uow(), Substitute.For<IAuditWriter>(), User(), new TestClock(Now))
                .HandleAsync(HiddenDocumentId, PeriodKey, CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", error.ErrorCode);
        Assert.True(exception.IsActiveAt(Now));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Requirement", "ФВ-1.10")]
    public async Task Нічого_відкритого_закриття_нічого_не_робить()
    {
        var store = Store(null);
        var audit = Substitute.For<IAuditWriter>();
        var uow = Uow();

        await new CloseDocumentPeriodReopenHandler(store, Access(withReopen: true), uow, audit, User(), new TestClock(Now))
            .HandleAsync(VisibleDocumentId, PeriodKey, CancellationToken.None);

        await uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }

    /// <remarks>
    /// B69 (G3): поки триває архівація проєкту, місяць для документа не відкривається — та сама відмова, що й в
    /// архівованому. Червоне без фіксу: <c>DenialOf</c> дивився лише <c>Archived</c> — виняток додавався. Контроль: ті
    /// самі факти без архівації — виняток додається.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "B69-G3")]
    public async Task Під_час_архівації_місяць_для_документа_не_відкривається(bool archiving)
    {
        var store = Substitute.For<IDocumentPeriodReopenStore>();
        store.GetFactsAsync(VisibleDocumentId, PeriodKey, Now, Arg.Any<CancellationToken>())
            .Returns(new DocumentPeriodFacts(
                ProjectId, "PRJ", ProjectStatus.Active, 1, "UTC", "DOC-1", PeriodState.Closed, IsArchiving: archiving));
        store.FindOpenAsync(VisibleDocumentId, PeriodKey, Arg.Any<CancellationToken>()).Returns((DocumentPeriodReopen?)null);

        var handler = new OpenDocumentPeriodReopenHandler(
            store, Access(withReopen: true), Uow(), Substitute.For<IAuditWriter>(), User(), new TestClock(Now));

        if (archiving)
        {
            var error = await Assert.ThrowsAsync<BusinessRuleException>(
                () => handler.HandleAsync(VisibleDocumentId, PeriodKey, "Лист №19", Now.AddHours(5), CancellationToken.None));

            Assert.Equal("ECR-PRD-0409", error.ErrorCode);
            Assert.Equal("err.ECR-PRD-0409.projectArchived", error.Details!["messageKey"]);
            store.DidNotReceiveWithAnyArgs().Add(default!);
        }
        else
        {
            await handler.HandleAsync(VisibleDocumentId, PeriodKey, "Лист №19", Now.AddHours(5), CancellationToken.None);
            store.Received(1).Add(Arg.Any<DocumentPeriodReopen>());
        }
    }

    /// <remarks>
    /// B81 (G3, F13 #877): архівація проєкту, закомічена між перевіркою фактів і замком періоду, — відкриття під замком
    /// бачить її і відмовляє. Детерміновано: перше читання фактів (до транзакції) — Active, друге (під замком) — Archived,
    /// як і буває, коли `ArchiveProjectHandler` комітить у проміжку. Червоне без фіксу: факти читалися один раз до
    /// транзакції — виняток додавався в уже архівований проєкт.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Finding", "B81-G3")]
    public async Task Архівація_між_перевіркою_і_замком_періоду_не_пропускає_виняток()
    {
        var store = Substitute.For<IDocumentPeriodReopenStore>();
        store.GetFactsAsync(VisibleDocumentId, PeriodKey, Now, Arg.Any<CancellationToken>())
            .Returns(
                new DocumentPeriodFacts(ProjectId, "PRJ", ProjectStatus.Active, 1, "UTC", "DOC-1", PeriodState.Closed),
                new DocumentPeriodFacts(ProjectId, "PRJ", ProjectStatus.Archived, 1, "UTC", "DOC-1", PeriodState.Closed));
        store.FindOpenAsync(VisibleDocumentId, PeriodKey, Arg.Any<CancellationToken>()).Returns((DocumentPeriodReopen?)null);
        var uow = Uow();

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new OpenDocumentPeriodReopenHandler(
                    store, Access(withReopen: true), uow, Substitute.For<IAuditWriter>(), User(), new TestClock(Now))
                .HandleAsync(VisibleDocumentId, PeriodKey, "Лист №20", Now.AddHours(5), CancellationToken.None));

        Assert.Equal("err.ECR-PRD-0409.projectArchived", error.Details!["messageKey"]);
        store.DidNotReceiveWithAnyArgs().Add(default!);
        await uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Підготовка ──────────────────────────────────────────────────────

    /// <summary>Сховище: факти закритого місяця і (за наявності) незакритий виняток.</summary>
    private static IDocumentPeriodReopenStore Store(DocumentPeriodReopen? open)
    {
        var store = Substitute.For<IDocumentPeriodReopenStore>();
        store.GetFactsAsync(Arg.Any<long>(), PeriodKey, Now, Arg.Any<CancellationToken>())
            .Returns(new DocumentPeriodFacts(ProjectId, "PRJ", ProjectStatus.Active, 1, "UTC", "DOC-1", PeriodState.Closed));
        store.FindOpenAsync(Arg.Any<long>(), PeriodKey, Arg.Any<CancellationToken>()).Returns(open);
        return store;
    }

    /// <summary>Доступ: проєкт видимий (Read), схований документ — невидимий; право — за потреби.</summary>
    private static IAccessDecisionService Access(bool withReopen)
    {
        var builder = new AccessBuilder { UserId = UserId }.Grant(ResourceKind.Project, ProjectId, GrantLevel.Read);
        if (withReopen)
        {
            builder.Permission(ReopenPeriodHandler.Permission);
        }

        var access = Substitute.For<IAccessDecisionService>();
        access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(builder.Build());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), VisibleDocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), HiddenDocumentId, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));
        return access;
    }

    private static IUnitOfWork Uow()
    {
        var uow = Substitute.For<IUnitOfWork>();
        uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<Func<CancellationToken, Task>>(0)(call.ArgAt<CancellationToken>(1)));
        return uow;
    }

    private static ICurrentUser User()
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(UserId);
        return user;
    }
}
