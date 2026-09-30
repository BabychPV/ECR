// tests/Ecr.Application.Tests/Integration/ListSourceEventsHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Таблиця подій джерела (HSE301 A6): видимість за правом читання документа без оракула, фільтри, час у поясі
/// проєкту, розбір приміток зв'язку, курсор.
/// </summary>
public sealed class ListSourceEventsHandlerTests
{
    private const int Actor = 9;
    private const int EntityId = 5;
    private const long VisibleDocument = 42;
    private const long HiddenDocument = 43;
    private static readonly DateTime Start = new(2026, 1, 31, 19, 0, 0, DateTimeKind.Utc);

    private readonly ISourceEventMapStore _store = Substitute.For<ISourceEventMapStore>();
    private readonly ICollectionStore _sources = Substitute.For<ICollectionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public ListSourceEventsHandlerTests()
    {
        _user.UserId.Returns(Actor);
        Profile();

        _sources.FindSourceEntityAsync(EntityId, Arg.Any<CancellationToken>())
            .Returns(new SourceEntity(1, "FlareEvent", RegistrySourceKind.External));
        _store.ListMapsAsync(EntityId, Arg.Any<CancellationToken>()).Returns([Map(10, VisibleDocument), Map(11, HiddenDocument)]);

        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), VisibleDocument, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), HiddenDocument, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));

        _store.ReadLinksAsync(Arg.Any<SourceEventLinkFilter>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SourceEventLinkPage([], 0));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task До_сховища_йдуть_лише_мапінги_документів_які_користувач_бачить_і_фільтри_як_є()
    {
        var filter = new SourceEventsFilter(
            EntityId, null, null, [SourceEventLinkStatus.Missing], Start, Start.AddDays(1), 202601);

        await Handler().HandleAsync(filter, new CursorRequest(25, "cur"), default);

        await _store.Received(1).ReadLinksAsync(
            Arg.Is<SourceEventLinkFilter>(f =>
                f.MapIds.Count == 1 && f.MapIds.Contains(10)
                && f.Statuses.Count == 1 && f.Statuses.Contains(SourceEventLinkStatus.Missing)
                && f.FromUtc == Start && f.ToUtc == Start.AddDays(1) && f.PeriodKey == 202601),
            "cur",
            25,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Невидимий_мапінг_за_mapId_це_404_як_неіснуючий_а_без_видимих_документів_порожньо()
    {
        var hidden = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(new SourceEventsFilter(EntityId, 11, null, null, null, null, null), new CursorRequest(), default));
        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(new SourceEventsFilter(EntityId, 999, null, null, null, null, null), new CursorRequest(), default));

        // Відмови не відрізняються: за ними не вгадати, що існує, а що лише заховане.
        Assert.Equal(hidden.Details!["messageKey"], missing.Details!["messageKey"]);
        Assert.Equal(hidden.ErrorCode, missing.ErrorCode);

        _access.CanReadDocumentAsync(Arg.Any<AccessProfile>(), VisibleDocument, Arg.Any<CancellationToken>())
            .Returns(EditDecision.Deny(EditDenyReason.NoGrant));
        var page = await Handler().HandleAsync(new SourceEventsFilter(EntityId, null, null, null, null, null, null), new CursorRequest(), default);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        await _store.DidNotReceiveWithAnyArgs().ReadLinksAsync(default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Сутності_немає_404_лише_тому_хто_читає_інтеграцію_а_решті_порожньо()
    {
        var unknown = new SourceEventsFilter(404, null, null, null, null, null, null);

        var plain = await Handler().HandleAsync(unknown, new CursorRequest(), default);
        Assert.Empty(plain.Items);

        Profile("Integration.View");
        await Assert.ThrowsAsync<NotFoundException>(() => Handler().HandleAsync(unknown, new CursorRequest(), default));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Рядок_несе_час_у_поясі_проєкту_і_utc_ключ_рядка_та_розібрані_примітки()
    {
        var link = SourceEventLink.FirstSeenWritten(
            10,
            new SourceEventObservation("E1", "Flaring", Start, Start.AddMinutes(15), null, " flare/fl-370 "),
            new SourceEventRowRef(202602, 777, "EF-E1"),
            "[\"VOLUME\"]",
            "[{\"column\":\"CATEGORY\",\"value\":\"V99\"}]",
            Start);
        Set(link, 1000);
        _store.ReadLinksAsync(Arg.Any<SourceEventLinkFilter>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new SourceEventLinkPage([new SourceEventLinkRow(link, VisibleDocument, "DOC-42", 10, "Asia/Atyrau")], 1));

        var page = await Handler().HandleAsync(new SourceEventsFilter(EntityId, null, null, null, null, null, null), new CursorRequest(), default);

        var row = Assert.Single(page.Items);
        Assert.Equal(SourceEventLinkStatus.Unmapped, row.Status);

        // 19:00Z 31 січня в Asia/Atyrau (+05:00) — 00:00 1 лютого: місяць за поясом проєкту, а не за UTC.
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.FromHours(5)), row.StartLocal);
        Assert.Equal(Start, row.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 2, 1, 0, 15, 0, TimeSpan.FromHours(5)), row.EndLocal);
        Assert.Equal(("EF-E1", 202602, 777L, "DOC-42"), (row.RowKey, row.PeriodKey, row.TableInstanceId, row.DocumentKey));
        Assert.Equal("FLARE/FL-370", row.PrimaryElement);
        Assert.Equal(["VOLUME"], row.KeptManual);
        Assert.Equal(new SourceEventUnmappedItem("CATEGORY", "V99"), Assert.Single(row.Unmapped));
        Assert.Equal(1, page.TotalCount);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Зіпсований_json_приміток_не_валить_сторінку_а_зайвий_рядок_дає_курсор()
    {
        var first = Link("E1", 1, "{broken");
        var second = Link("E2", 2, null);
        _store.NextCursor(first).Returns("next");
        _store.ReadLinksAsync(Arg.Any<SourceEventLinkFilter>(), Arg.Any<string?>(), 1, Arg.Any<CancellationToken>())
            .Returns(new SourceEventLinkPage(
                [Row(first), Row(second)], 5));

        var page = await Handler().HandleAsync(new SourceEventsFilter(EntityId, null, null, null, null, null, null), new CursorRequest(1), default);

        // Лімит 1: перший рядок віддано, другий (запасний) лише вказує, що сторінка не остання.
        Assert.Equal("E1", Assert.Single(page.Items).SourceEventId);
        Assert.Empty(page.Items[0].Unmapped);
        // Курсор — за ОСТАННІМ ВІДДАНИМ рядком, а не запасним.
        Assert.Equal((5, "next"), (page.TotalCount, page.NextCursor));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Розмір_сторінки_поза_межами_422(int limit)
    {
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(new SourceEventsFilter(EntityId, null, null, null, null, null, null), new CursorRequest(limit), default));

        Assert.Equal("err.ECR-REQ-0422.pageSizeOutOfRange", refused.Details!["messageKey"]);
    }

    private ListSourceEventsHandler Handler() => new(_store, _sources, _access, _user);

    private void Profile(params string[] permissions)
    {
        var builder = new AccessBuilder { UserId = Actor };
        foreach (var permission in permissions)
        {
            builder = builder.Permission(permission);
        }

        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private static SourceEventLinkRow Row(SourceEventLink link) => new(link, VisibleDocument, "DOC-42", 10, "Asia/Atyrau");

    private static SourceEventLink Link(string eventId, long id, string? unmapped)
    {
        var link = SourceEventLink.FirstSeenWritten(
            10,
            new SourceEventObservation(eventId, null, Start, Start.AddMinutes(1), null),
            new SourceEventRowRef(202601, 1, $"EF-{eventId}"),
            null,
            unmapped,
            Start);
        Set(link, id);
        return link;
    }

    private static void Set(SourceEventLink link, long id)
        => typeof(Entity<long>).GetProperty(nameof(Entity<long>.Id))!.SetValue(link, id);

    private static SourceEventMap Map(int id, long documentId)
    {
        var table = new TableDef(
            sheetDefId: 1, EcrCode.Create("FLARE_EVENTS"), Text("Flare events"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Dynamic);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(table, 10);

        ColumnDef Column(string code, int columnId)
        {
            var column = new ColumnDef(10, EcrCode.Create(code), Text(code), columnId, CellDataType.Date);
            typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(column, columnId);
            return column;
        }

        var map = SourceEventMap.Create(
            EntityId, documentId, table, [new(Column("START_AT", 1), "$start"), new(Column("END_AT", 2), "$end")],
            SourceEventVolumeMode.None);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(map, id);
        return map;
    }

    private static LocalizedText Text(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
