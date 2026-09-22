// tests/Ecr.Application.Tests/Documents/ListDocumentsGrantScopeTests.cs
using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Documents;

/// <summary>
/// Межа видимості переліку документів. Перелік документів чужого проєкту — це
/// вже відомості про те, які об'єкти звітують і як часто; те саме стосується
/// їхньої КІЛЬКОСТІ (аудит 2026-09-16, §3.3).
/// </summary>
public sealed class ListDocumentsGrantScopeTests
{
    private const int Mine = 10;
    private const int Foreign = 11;

    private readonly IDocumentStore _documents = Substitute.For<IDocumentStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ListDocumentsGrantScopeTests()
    {
        _user.UserId.Returns(7);

        _access.BuildProfileAsync(7, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = 7 }
                .Permission(ListDocumentsHandler.Permission)
                .Grant(ResourceKind.Project, Mine, GrantLevel.Read)
                .Grant(ResourceKind.Project, Foreign, GrantLevel.Read)
                .Deny(ResourceKind.Project, Foreign)
                .Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Гранти_передаються_у_ЗАПИТ_а_не_лише_фільтрують_відповідь()
    {
        // ⛔ Саме те, що ламалося: постфільтр у обробнику прибирав чужі
        // документи з `Items`, але запит до сховища йшов БЕЗ жодної межі —
        // тож і `TotalCount` рахувався по всіх проєктах системи, і сторінка
        // «худнула» на видалених елементах, поки `NextCursor` вказував далі в
        // нефільтрованій послідовності.
        Page(new PagedResult<DocumentSummary>([Doc(1, Mine)], NextCursor: null, TotalCount: null));

        await Handler().HandleAsync(projectId: null, periodKey: null, state: null, mine: false, hasLateEdits: null, new CursorRequest(Limit: 50), default);

        await _documents.Received(1).ListAsync(
            Arg.Any<int?>(),
            Arg.Any<PeriodKeyFilter>(),
            Arg.Any<DocumentListFilter>(),
            Arg.Any<CursorRequest>(),
            Arg.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.Count == 1 && ids.Contains(Mine)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Заборонений_проєкт_не_потрапляє_у_межу_видимості()
    {
        // Явна заборона виграє над грантом (ФВ-6.6) — і мусить виграти саме в
        // побудові межі запиту, інакше документи забороненого проєкту дійшли б
        // до сховища й далі трималися лише на постфільтрі.
        Page(new PagedResult<DocumentSummary>([], NextCursor: null, TotalCount: null));

        await Handler().HandleAsync(projectId: null, periodKey: null, state: null, mine: false, hasLateEdits: null, new CursorRequest(Limit: 50), default);

        await _documents.Received(1).ListAsync(
            Arg.Any<int?>(),
            Arg.Any<PeriodKeyFilter>(),
            Arg.Any<DocumentListFilter>(),
            Arg.Any<CursorRequest>(),
            Arg.Is<IReadOnlyCollection<int>?>(ids => ids != null && !ids.Contains(Foreign)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task TotalCount_сховища_не_витікає_у_відповідь_як_є()
    {
        // Навіть якщо сховище колись почне рахувати `TotalCount`, обробник не
        // має права віддати НЕфільтроване число: «1 з 5000» розкрило б,
        // скільки документів у проєктах, до яких доступу немає.
        Page(new PagedResult<DocumentSummary>(
            [Doc(1, Mine), Doc(2, Foreign)], NextCursor: null, TotalCount: 5000));

        var result = await Handler()
            .HandleAsync(projectId: null, periodKey: null, state: null, mine: false, hasLateEdits: null, new CursorRequest(Limit: 50), default);

        var visible = Assert.Single(result.Items);
        Assert.Equal(Mine, visible.ProjectId);

        // Точна кількість видимого, а не 5000 з усієї системи.
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Незавершена_послідовність_не_вигадує_загальної_кількості()
    {
        // Коли є наступна сторінка, точної кількості обробник НЕ знає — і
        // `null` («підрахунок недоступний») тут честніше за число зі сторінки.
        Page(new PagedResult<DocumentSummary>([Doc(1, Mine)], NextCursor: "c1", TotalCount: null));

        var result = await Handler()
            .HandleAsync(projectId: null, periodKey: null, state: null, mine: false, hasLateEdits: null, new CursorRequest(Limit: 1), default);

        Assert.Null(result.TotalCount);
        Assert.Equal("c1", result.NextCursor);
    }

    private ListDocumentsHandler Handler() => new(_documents, _access, _user);

    private void Page(PagedResult<DocumentSummary> result)
        => _documents.ListAsync(
                Arg.Any<int?>(),
                Arg.Any<PeriodKeyFilter>(),
                Arg.Any<DocumentListFilter>(),
                Arg.Any<CursorRequest>(),
                Arg.Any<IReadOnlyCollection<int>?>(),
                Arg.Any<CancellationToken>())
            .Returns(result);

    private static DocumentSummary Doc(long id, int projectId)
        => new(id, projectId, $"DOC-{id}", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1,
            new Dictionary<string, string>(StringComparer.Ordinal));
}
