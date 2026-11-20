// tests/Ecr.Application.Tests/Workflow/MonthDecisionRightsTests.cs
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// D-6: права над КОЖНИМ місяцем річного аркуша в місячних колонках <c>GET …/submission-target</c> — затвердити/відхилити
/// (<c>CanDecide</c>), відкликати (<c>CanRecall</c>), повернути затверджений (<c>CanReturn</c>) — тим самим правилом, що
/// й дія, окремо для кожного місяця, одним пакетом рішень доступу.
/// </summary>
/// <remarks>
/// ⛔ До фіксу клієнт питав право лише для одного (обраного) місяця й вважав кожен поданий місяць доступним. Мутації:
/// підставити одне рішення для всіх місяців — перший тест червоний; не відкидати закритих місяців (M1) — «закритий
/// місяць» червоний; не питати режим погодження (L1) — «версія без погодження» червоний.
/// </remarks>
public sealed class MonthDecisionRightsTests
{
    private const long Document = 500;
    private const int Sheet = 42;
    private const int Me = 9;
    private const int Other = 5;
    private const int Feb = 202602;
    private const int Mar = 202603;
    private const int Apr = 202604;
    private const int May = 202605;
    private const int Jun = 202606;
    private const int Jul = 202607;
    private const int Aug = 202608;

    private static SubmissionCalendar Calendar(ProjectStatus status = ProjectStatus.Active, bool archiving = false) => new(
        status,
        archiving,
        PeriodKind.Monthly,
        [
            // Лютий затверджено, але місяць закрито; березень затверджено й відкрито.
            new SubmissionCalendarPeriod(Feb, PeriodState.Closed, DocumentStatus.Approved, Other, null),
            new SubmissionCalendarPeriod(Mar, PeriodState.Open, DocumentStatus.Approved, Other, null),
            // Квітень подав інший; травень — я, на першому кроці (маршруту немає: перший крок null).
            new SubmissionCalendarPeriod(Apr, PeriodState.Open, DocumentStatus.Submitted, Other, null),
            new SubmissionCalendarPeriod(May, PeriodState.Open, DocumentStatus.Submitted, Me, null),
            new SubmissionCalendarPeriod(Jun, PeriodState.Open, DocumentStatus.Draft),
            // Липень подав я, але крок уже підписано (поточний крок ≠ першого).
            new SubmissionCalendarPeriod(Jul, PeriodState.Open, DocumentStatus.Submitted, Me, 33),
            // Серпень подав я на першому кроці, але місяць закрито.
            new SubmissionCalendarPeriod(Aug, PeriodState.Closed, DocumentStatus.Submitted, Me, null),
        ]);

    private static IReadOnlyList<MonthColumnStatusDto> Columns(SubmissionCalendar calendar, params int[] keys)
        => [.. keys.Select(k => new MonthColumnStatusDto(
            7, $"M{k % 100}", (byte)(k % 100), k, calendar.Periods.Single(p => p.PeriodKey == k).SheetStatus))];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    public async Task Право_рахується_для_кожного_місяця_окремо()
    {
        var f = Arrange(reopenPermission: true);

        var by = (await Apply(f, Calendar(), Feb, Mar, Apr, May, Jun, Jul, Aug)).ToDictionary(c => c.PeriodKey);

        // Квітень — крок не мій; травень — мій.
        Assert.False(by[Apr].CanDecide);
        Assert.True(by[May].CanDecide);
        // Відкликати — лише власне подання на першому кроці у відкритому місяці.
        Assert.False(by[Apr].CanRecall);
        Assert.True(by[May].CanRecall);
        Assert.False(by[Jul].CanRecall);
        Assert.False(by[Aug].CanRecall);
        // Повернути — лише затверджений відкритий березень.
        Assert.True(by[Mar].CanReturn);
        Assert.False(by[Feb].CanReturn);
        Assert.False(by[Apr].CanReturn);
        // Чернетка — нічого.
        Assert.Equal((false, false, false), (by[Jun].CanDecide, by[Jun].CanRecall, by[Jun].CanReturn));

        // ОДИН пакет: затвердження — подані місяці; грант відкликання — лише власне подання на першому кроці у відкритому
        // місяці; повернення — лише затверджений відкритий місяць. Чернетка не питається.
        await f.Access.Received(1).SheetPeriodRightsAsync(
            f.Profile, Document, Sheet, null,
            Arg.Is<IReadOnlyCollection<PeriodKey>>(k => Keys(k).SequenceEqual(new[] { Apr, May, Jul, Aug })),
            Arg.Is<IReadOnlyCollection<PeriodKey>>(k => Keys(k).SequenceEqual(new[] { May })),
            Arg.Is<IReadOnlyCollection<PeriodKey>>(k => Keys(k).SequenceEqual(new[] { Mar })),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    public async Task Закритий_місяць_не_відкликається_й_не_повертається_затвердити_можна()
    {
        var f = Arrange(reopenPermission: true);

        var by = (await Apply(f, Calendar(), Feb, Aug)).ToDictionary(c => c.PeriodKey);

        Assert.False(by[Feb].CanReturn);
        Assert.False(by[Aug].CanRecall);
        // Затвердження ворота закритого періоду не мають — Approve на закритому місяці лишається (рішення сервера).
        Assert.True(by[Aug].CanDecide);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    [InlineData(ProjectStatus.Archived, false)]
    [InlineData(ProjectStatus.Active, true)]
    public async Task Архівний_чи_архівований_проєкт_ні_відкликання_ні_повернення(ProjectStatus status, bool archiving)
    {
        var f = Arrange(reopenPermission: true);

        var by = (await Apply(f, Calendar(status, archiving), Mar, May)).ToDictionary(c => c.PeriodKey);

        Assert.False(by[Mar].CanReturn);
        Assert.False(by[May].CanRecall);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    public async Task Версія_без_погодження_ні_затвердження_ні_відкликання_повернення_лишається()
    {
        var f = Arrange(reopenPermission: true, workflowNone: [May]);

        var by = (await Apply(f, Calendar(), Mar, May)).ToDictionary(c => c.PeriodKey);

        Assert.False(by[May].CanDecide);
        Assert.False(by[May].CanRecall);
        Assert.True(by[Mar].CanReturn);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    public async Task Колонки_одного_місяця_різних_таблиць_мають_однакові_права()
    {
        var f = Arrange(reopenPermission: false);
        IReadOnlyList<MonthColumnStatusDto> columns =
        [
            new(7, "MAY", 5, May, DocumentStatus.Submitted),
            new(8, "MAY_B", 5, May, DocumentStatus.Submitted),
        ];

        var result = await MonthDecisionRights.ApplyAsync(
            columns, Calendar(), f.Access, f.Workflow, f.Documents, f.Profile, Me, Document, Sheet, null, CancellationToken.None);

        Assert.All(result, c => Assert.True(c.CanDecide && c.CanRecall));
        // Один місяць у пакеті, а не по колонці.
        await f.Access.Received(1).SheetPeriodRightsAsync(
            f.Profile, Document, Sheet, null,
            Arg.Is<IReadOnlyCollection<PeriodKey>>(k => Keys(k).SequenceEqual(new[] { May })),
            Arg.Any<IReadOnlyCollection<PeriodKey>>(),
            Arg.Any<IReadOnlyCollection<PeriodKey>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    public async Task Без_права_Document_Reopen_повернути_не_можна_і_рішення_не_питається()
    {
        var f = Arrange(reopenPermission: false);

        var result = await Apply(f, Calendar(), Mar);

        Assert.False(Assert.Single(result).CanReturn);
        Assert.DoesNotContain(
            f.Access.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IAccessDecisionService.SheetPeriodRightsAsync));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    public async Task Лише_чернетки_жодного_запиту()
    {
        var f = Arrange(reopenPermission: true);

        var result = await Apply(f, Calendar(), Jun);

        Assert.False(Assert.Single(result).CanDecide);
        Assert.Empty(f.Access.ReceivedCalls());
        Assert.Empty(f.Documents.ReceivedCalls());
    }

    private static int[] Keys(IReadOnlyCollection<PeriodKey> keys) => [.. keys.Select(k => k.Value).Order()];

    private static Task<IReadOnlyList<MonthColumnStatusDto>> Apply(Fixture f, SubmissionCalendar calendar, params int[] keys)
        => MonthDecisionRights.ApplyAsync(
            Columns(calendar, keys), calendar, f.Access, f.Workflow, f.Documents, f.Profile, Me, Document, Sheet, null,
            CancellationToken.None);

    private sealed record Fixture(
        IAccessDecisionService Access, IWorkflowStore Workflow, IDocumentStore Documents, AccessProfile Profile);

    /// <summary>Пакет рішень доступу: затвердити можна травень і серпень, грант подання — є, повернути — можна.</summary>
    private static Fixture Arrange(bool reopenPermission, IReadOnlyCollection<int>? workflowNone = null)
    {
        var builder = new AccessBuilder { UserId = Me }.Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve);
        if (reopenPermission)
        {
            builder.Permission(ReopenDocumentHandler.Permission);
        }

        var profile = builder.Build();

        var access = Substitute.For<IAccessDecisionService>();
        access.SheetPeriodRightsAsync(
                profile, Document, Sheet, null,
                Arg.Any<IReadOnlyCollection<PeriodKey>>(), Arg.Any<IReadOnlyCollection<PeriodKey>>(),
                Arg.Any<IReadOnlyCollection<PeriodKey>>(), Arg.Any<CancellationToken>())
            .Returns(call => new SheetPeriodRights(
                call.ArgAt<IReadOnlyCollection<PeriodKey>>(4).Select(k => k.Value).Where(k => k is May or Aug).ToHashSet(),
                call.ArgAt<IReadOnlyCollection<PeriodKey>>(5).Select(k => k.Value).ToHashSet(),
                call.ArgAt<IReadOnlyCollection<PeriodKey>>(6).Select(k => k.Value).ToHashSet()));
        access.DocumentProjectIdAsync(Document, Arg.Any<CancellationToken>()).Returns(AccessBuilder.ProjectId);

        var documents = Substitute.For<IDocumentStore>();
        documents.FindProjectIdAsync(Document, Arg.Any<CancellationToken>()).Returns(AccessBuilder.ProjectId);
        IReadOnlySet<int> none = (workflowNone ?? []).ToHashSet();
        documents.GetWorkflowNonePeriodsAsync(Document, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(none);

        var workflow = Substitute.For<IWorkflowStore>();

        return new Fixture(access, workflow, documents, profile);
    }
}
