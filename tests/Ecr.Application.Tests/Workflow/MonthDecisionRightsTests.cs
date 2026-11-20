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
/// й дія, окремо для кожного місяця.
/// </summary>
/// <remarks>
/// ⛔ До фіксу клієнт питав право лише для одного (обраного) місяця й вважав кожен поданий місяць доступним: квітень на
/// кроці 2 (не мій) ставав ціллю за замовчуванням, кнопок не було, а травень (крок 1, мій) був недосяжний. Мутація: у
/// <see cref="MonthDecisionRights"/> підставити одне рішення для всіх місяців — перший тест червоний.
/// </remarks>
public sealed class MonthDecisionRightsTests
{
    private const long Document = 500;
    private const int Sheet = 42;
    private const int Me = 9;
    private const int Other = 5;
    private const int Mar = 202603;
    private const int Apr = 202604;
    private const int May = 202605;
    private const int Jun = 202606;
    private const int Jul = 202607;

    private static readonly SubmissionCalendar Calendar = new(
        ProjectStatus.Active,
        false,
        PeriodKind.Monthly,
        [
            new SubmissionCalendarPeriod(Mar, PeriodState.Open, DocumentStatus.Approved, Other, null),
            // Квітень подав інший; травень — я, на першому кроці (маршруту немає: перший крок null).
            new SubmissionCalendarPeriod(Apr, PeriodState.Open, DocumentStatus.Submitted, Other, null),
            new SubmissionCalendarPeriod(May, PeriodState.Open, DocumentStatus.Submitted, Me, null),
            new SubmissionCalendarPeriod(Jun, PeriodState.Open, DocumentStatus.Draft),
            // Липень подав я, але крок уже підписано (поточний крок ≠ першого).
            new SubmissionCalendarPeriod(Jul, PeriodState.Open, DocumentStatus.Submitted, Me, 33),
        ]);

    private static IReadOnlyList<MonthColumnStatusDto> Columns(params int[] keys)
        => [.. keys.Select(k => new MonthColumnStatusDto(7, $"M{k % 100}", (byte)(k % 100), k, StatusOf(k)))];

    private static DocumentStatus StatusOf(int key) => Calendar.Periods.Single(p => p.PeriodKey == key).SheetStatus;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    public async Task Право_рахується_для_кожного_місяця_окремо()
    {
        var (access, workflow, documents, profile) = Arrange(reopenPermission: true);

        var result = await MonthDecisionRights.ApplyAsync(
            Columns(Mar, Apr, May, Jun, Jul), Calendar, access, workflow, documents, profile, Me, Document, Sheet, null,
            CancellationToken.None);

        var by = result.ToDictionary(c => c.PeriodKey);
        // Квітень — крок не мій; травень — мій.
        Assert.False(by[Apr].CanDecide);
        Assert.True(by[May].CanDecide);
        // Відкликати — лише власне подання на першому кроці.
        Assert.False(by[Apr].CanRecall);
        Assert.True(by[May].CanRecall);
        Assert.False(by[Jul].CanRecall);
        // Повернути — лише затверджений березень.
        Assert.True(by[Mar].CanReturn);
        Assert.False(by[Apr].CanReturn);
        // Чернетка — нічого.
        Assert.Equal((false, false, false), (by[Jun].CanDecide, by[Jun].CanRecall, by[Jun].CanReturn));

        // Чернетка не коштує запиту; грант відкликання питається лише для власного подання на першому кроці.
        await access.DidNotReceive().CanApproveAsync(profile, Document, Sheet, new PeriodKey(Jun), Arg.Any<CancellationToken>());
        await access.Received(1).HasSubmitGrantAsync(profile, Document, Sheet, new PeriodKey(May), null, Arg.Any<CancellationToken>());
        await access.DidNotReceive().HasSubmitGrantAsync(profile, Document, Sheet, new PeriodKey(Apr), null, Arg.Any<CancellationToken>());
        await access.DidNotReceive().HasSubmitGrantAsync(profile, Document, Sheet, new PeriodKey(Jul), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    public async Task Колонки_одного_місяця_різних_таблиць_мають_однакові_права()
    {
        var (access, workflow, documents, profile) = Arrange(reopenPermission: false);
        IReadOnlyList<MonthColumnStatusDto> columns =
        [
            new(7, "MAY", 5, May, DocumentStatus.Submitted),
            new(8, "MAY_B", 5, May, DocumentStatus.Submitted),
        ];

        var result = await MonthDecisionRights.ApplyAsync(
            columns, Calendar, access, workflow, documents, profile, Me, Document, Sheet, null, CancellationToken.None);

        Assert.All(result, c => Assert.True(c.CanDecide && c.CanRecall));
        // Одне рішення на місяць, а не на колонку.
        await access.Received(1).CanApproveAsync(profile, Document, Sheet, new PeriodKey(May), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    public async Task Без_права_Document_Reopen_повернути_не_можна_і_рішення_не_питається()
    {
        var (access, workflow, documents, profile) = Arrange(reopenPermission: false);

        var result = await MonthDecisionRights.ApplyAsync(
            Columns(Mar), Calendar, access, workflow, documents, profile, Me, Document, Sheet, null, CancellationToken.None);

        Assert.False(Assert.Single(result).CanReturn);
        await access.DidNotReceive().CanReopenAsync(
            Arg.Any<AccessProfile>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait("Decision", "D-559")]
    public async Task Лише_чернетки_жодного_запиту()
    {
        var (access, workflow, documents, profile) = Arrange(reopenPermission: true);

        var result = await MonthDecisionRights.ApplyAsync(
            Columns(Jun), Calendar, access, workflow, documents, profile, Me, Document, Sheet, null, CancellationToken.None);

        Assert.False(Assert.Single(result).CanDecide);
        Assert.Empty(access.ReceivedCalls());
        Assert.Empty(documents.ReceivedCalls());
    }

    private static (IAccessDecisionService Access, IWorkflowStore Workflow, IDocumentStore Documents, AccessProfile Profile)
        Arrange(bool reopenPermission)
    {
        var builder = new AccessBuilder { UserId = Me }.Grant(ResourceKind.Project, AccessBuilder.ProjectId, GrantLevel.Approve);
        if (reopenPermission)
        {
            builder.Permission(ReopenDocumentHandler.Permission);
        }

        var profile = builder.Build();

        var access = Substitute.For<IAccessDecisionService>();
        access.CanApproveAsync(profile, Document, Sheet, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<PeriodKey>(3).Value == May
                ? EditDecision.Allow()
                : EditDecision.Deny(EditDenyReason.InsufficientGrantLevel));
        access.CanReopenAsync(profile, Document, Sheet, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(EditDecision.Allow());
        access.HasSubmitGrantAsync(profile, Document, Sheet, Arg.Any<PeriodKey>(), null, Arg.Any<CancellationToken>())
            .Returns(true);
        access.DocumentProjectIdAsync(Document, Arg.Any<CancellationToken>()).Returns(AccessBuilder.ProjectId);

        var documents = Substitute.For<IDocumentStore>();
        documents.FindProjectIdAsync(Document, Arg.Any<CancellationToken>()).Returns(AccessBuilder.ProjectId);

        var workflow = Substitute.For<IWorkflowStore>();

        return (access, workflow, documents, profile);
    }
}
