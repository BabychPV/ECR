using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Workflow.ReturnedSheets;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Workflow;

/// <summary>
/// ✎ D-449: правило «чия це задача» блоку «Повернуто на доопрацювання» в пам'яті обробника — власник, учасник
/// <c>Write</c>/<c>Submit</c> на аркуш, заборона проєкту, симуляція, бюджет запитів.
/// </summary>
/// <remarks>
/// Мутаційний доказ (локально, не в коміті; прогін — CI): прибрати <c>row.OwnerUserId == userId</c> → червоніє
/// <c>Чужий_документ_проєкту_не_задача</c>; замінити <c>WorksOnSheet</c> на «будь-яке надання» → червоніють випадки
/// <c>Read</c>/<c>Approve</c>/інший аркуш; прибрати <c>DeniesProject</c> у циклі → червоніє заборона; прибрати перевірку
/// <c>HasDocumentParticipations</c> перед <c>ActiveParticipationsAsync</c> → червоніє бюджет.
/// </remarks>
public sealed class ListMyReturnedSheetsHandlerTests
{
    private const int UserId = 7;
    private const int OtherUserId = 8;
    private const int ProjectId = AccessBuilder.ProjectId;
    private const int SheetId = AccessBuilder.SheetId;
    private const int Period = 202601;
    private const long DocumentId = 55;

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IApprovalQueueStore _store = Substitute.For<IApprovalQueueStore>();
    private readonly IMetadataCache _metadata = Substitute.For<IMetadataCache>();

    public ListMyReturnedSheetsHandlerTests()
    {
        _user.UserId.Returns(UserId);
        _store.ListProjectsAsync(Arg.Any<CancellationToken>()).Returns(
            [new ApprovalQueueProject(ProjectId, "P10", 1, "T1", ProjectStatus.Active, false)]);

        var sheet = new SheetDef(1, EcrCode.Create("S1"), new LocalizedText(new Dictionary<string, string> { ["en"] = "Sheet 1" }), 1);
        typeof(Entity<int>).GetProperty("Id")!.SetValue(sheet, SheetId);
        _metadata.GetAsync(1, Arg.Any<CancellationToken>()).Returns(new TemplateVersionSnapshot(
            1, 0, [sheet], new Dictionary<int, ColumnDef>(), new Dictionary<(int, string), RowDef>()));
    }

    [Fact]
    [Trait("Decision", "D-449")]
    public async Task Власник_бачить_повернення_з_причиною_і_ім_ям()
    {
        Profile(participant: false, projectLevel: GrantLevel.Submit);
        Rows(Row(owner: UserId));

        var page = await Handler().HandleAsync(null, null, default);

        var item = Assert.Single(page.Items);
        Assert.Equal(DocumentId, item.DocumentId);
        Assert.Equal("S1", item.SheetCode);
        Assert.Equal("Sheet 1", item.SheetNameL10n.Get("en"));
        Assert.Equal("Fix totals", item.Reason);
        Assert.Equal("Approver", item.ReturnedByDisplayName);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    [Trait("Decision", "D-449")]
    public async Task Чужий_документ_проєкту_не_задача()
    {
        // ⛔ Той самий проєкт, те саме право — але документ іншого власника і надання немає.
        Profile(participant: false, projectLevel: GrantLevel.Approve);
        Rows(Row(owner: OtherUserId));

        Assert.Empty((await Handler().HandleAsync(null, null, default)).Items);
    }

    [Theory]
    [Trait("Decision", "D-449")]
    [InlineData(GrantLevel.Write, null, true)]
    [InlineData(GrantLevel.Submit, "S1", true)]
    [InlineData(GrantLevel.Read, null, false)]
    [InlineData(GrantLevel.Approve, null, false)]
    [InlineData(GrantLevel.Submit, "S2", false)]
    public async Task Учасник_лише_з_Write_або_Submit_на_цей_аркуш(GrantLevel level, string? sheetCode, bool expected)
    {
        Profile(participant: true, projectLevel: null);
        Participations(new ParticipationGrant(level, sheetCode, null, null));
        Rows(Row(owner: OtherUserId));

        var page = await Handler().HandleAsync(null, null, default);

        Assert.Equal(expected ? 1 : 0, page.Items.Count);
    }

    [Fact]
    [Trait("Decision", "D-449")]
    public async Task Заборона_проєкту_перемагає_власника_й_учасника()
    {
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = UserId, Participant = true }
                .Grant(ResourceKind.Project, ProjectId, GrantLevel.Submit)
                .Deny(ResourceKind.Project, ProjectId)
                .Build());
        Participations(new ParticipationGrant(GrantLevel.Write, null, null, null));
        Rows(Row(owner: UserId));

        Assert.Empty((await Handler().HandleAsync(null, null, default)).Items);
    }

    [Fact]
    [Trait("Decision", "D-449")]
    public async Task Симуляція_порожня_без_звернень_до_сховища()
    {
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(
            new AccessBuilder { UserId = UserId }.Grant(ResourceKind.Project, ProjectId, GrantLevel.Submit)
                .Build(simulation: true, simulatedFor: OtherUserId));

        Assert.Empty((await Handler().HandleAsync(null, null, default)).Items);
        await _store.DidNotReceiveWithAnyArgs().ListReturnedAsync(default, default!, default, default, default, default!, default);
    }

    [Fact]
    [Trait("Decision", "D-449")]
    public async Task Без_надань_не_читає_надань_і_передає_лише_проєкти()
    {
        Profile(participant: false, projectLevel: GrantLevel.Submit);
        Rows();

        await Handler().HandleAsync(null, null, default);

        await _access.DidNotReceiveWithAnyArgs().ActiveParticipationsAsync(default!, default);
        await _store.Received(1).ListReturnedAsync(
            UserId,
            Arg.Is<IReadOnlyCollection<int>>(p => p.Count == 1 && p.Contains(ProjectId)),
            Arg.Any<bool>(), Arg.Any<ReturnedSheetsCursor?>(), Arg.Any<int>(),
            Arg.Is<IReadOnlyCollection<long>>(d => d.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Decision", "D-449")]
    public async Task Зіпсований_курсор_422()
    {
        Profile(participant: false, projectLevel: GrantLevel.Submit);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => Handler().HandleAsync(null, "nope", default));
        Assert.Equal("err.ECR-REQ-0422.returnedSheetsCursorInvalid", error.Details!["messageKey"]);
    }

    private ListMyReturnedSheetsHandler Handler() => new(_store, _access, _metadata, _user);

    private void Profile(bool participant, GrantLevel? projectLevel)
    {
        var builder = new AccessBuilder { UserId = UserId, Participant = participant }
            .Permission(ListDocumentsHandler.Permission);
        if (projectLevel is { } level)
        {
            builder.Grant(ResourceKind.Project, ProjectId, level);
        }

        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>()).Returns(builder.Build());
    }

    private void Participations(ParticipationGrant grant)
        => _access.ActiveParticipationsAsync(Arg.Any<AccessProfile>(), Arg.Any<CancellationToken>()).Returns(
            new Dictionary<long, IReadOnlyList<ParticipationGrant>> { [DocumentId] = [grant] });

    private void Rows(params ReturnedSheetCandidate[] rows)
        => _store.ListReturnedAsync(
                Arg.Any<int>(), Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<bool>(), Arg.Any<ReturnedSheetsCursor?>(),
                Arg.Any<int>(), Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(rows);

    private static ReturnedSheetCandidate Row(int owner)
        => new(
            StateId: 1,
            DocumentId,
            ProjectId,
            DocumentKey: "DOC-55",
            DocumentNameL10n: null,
            OwnerUserId: owner,
            SheetId,
            Period,
            ReturnedAt: new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
            ReturnedByDisplayName: "Approver",
            Reason: "Fix totals");
}
