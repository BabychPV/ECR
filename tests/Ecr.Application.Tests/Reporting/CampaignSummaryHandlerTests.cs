using Ecr.Application.Common;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Reporting;
using Ecr.Application.Reporting.Dto;
using Ecr.Application.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Reporting;

/// <summary>Огляд кампанії: право, стеля переліку й відсутність межі проєктів (<c>BE-22</c>).</summary>
public sealed class CampaignSummaryHandlerTests
{
    private const int Mine = 10;
    private const int Foreign = 11;

    private readonly ICampaignSummaryStore _store = Substitute.For<ICampaignSummaryStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public CampaignSummaryHandlerTests()
    {
        _user.UserId.Returns(7);
        _store.ListAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => new CampaignProjectPage(
                Total: 2, Projects: [Row(Mine), Row(Foreign)], Buckets: []));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-22")]
    public async Task Право_перегляду_документів_огляду_кампанії_НЕ_відкриває()
    {
        // ⛔ Саме `Document.View`, а не безправний користувач: інакше тест
        // доводив би лише те, що маршрут закритий для стороннього, і не
        // розрізняв би «бачу свої документи» та «бачу кампанію цілком».
        Profile(new AccessBuilder { UserId = 7 }
            .Permission(ListDocumentsHandler.Permission)
            .Grant(ResourceKind.Project, Mine, GrantLevel.Read));

        var denied = await Assert.ThrowsAsync<AccessDeniedException>(
            () => Handler().HandleAsync(202601, default));

        Assert.Equal(GetCampaignSummaryHandler.Permission, denied.Details?["permission"]);
        await _store.DidNotReceiveWithAnyArgs().ListAsync(default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-22")]
    public async Task Огляд_віддає_проєкт_без_гранту_бо_межею_є_право_а_не_грант()
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ на рівні обробника. Додати в
        // `GetCampaignSummaryHandler` межу проєктів
        // (`ListDocumentsHandler.ReadableProjects(profile)`) — і червоним стає
        // рівно це твердження: `Foreign` зникне з відповіді.
        //
        // ⚠ Рішення людини `Q15-07` саме таке: доступ дає окреме право
        // `Report.ViewCampaign`, а не грант на проєкт. Кампанія без чужих
        // проєктів не відповідає на питання, заради якого існує.
        Profile(new AccessBuilder { UserId = 7 }
            .Permission(GetCampaignSummaryHandler.Permission)
            .Grant(ResourceKind.Project, Mine, GrantLevel.Read));

        var summary = await Handler().HandleAsync(202601, default);

        Assert.Equal([Mine, Foreign], summary.Projects.Select(p => p.ProjectId));
        Assert.Equal(202601, summary.PeriodKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-22")]
    public async Task Стеля_переліку_двісті_і_саме_її_обробник_просить_у_сховища()
    {
        // ⛔ Число ЛІТЕРАЛОМ, а не `Handler.MaxProjects`: твердження проти
        // константи з того самого модуля рухається разом із нею, і заміна 200
        // на 20 000 лишила б набір зеленим (урок 2026-09-19).
        Assert.Equal(200, GetCampaignSummaryHandler.MaxProjects);

        Profile(new AccessBuilder { UserId = 7 }.Permission(GetCampaignSummaryHandler.Permission));

        await Handler().HandleAsync(202601, default);

        await _store.Received(1).ListAsync(202601, 200, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "BE-22")]
    public async Task Період_нуль_відхиляється_до_звернення_до_сховища()
    {
        // ⚠ `A7-28` у мініатюрі: незв'язаний параметр запиту дає 0, і огляд за
        // періодом 0 повернув би порожню кампанію — зелену відповідь, яка
        // нічого не означає.
        Profile(new AccessBuilder { UserId = 7 }.Permission(GetCampaignSummaryHandler.Permission));

        await Assert.ThrowsAsync<Ecr.Domain.Abstractions.DomainException>(
            () => Handler().HandleAsync(0, default));

        await _store.DidNotReceiveWithAnyArgs().ListAsync(default, default, default);
    }

    private static CampaignProjectFacts Row(int projectId)
        => new(
            projectId,
            $"P{projectId}",
            new LocalizedText(new Dictionary<string, string> { ["en"] = $"Project {projectId}" }),
            Documents: 1, Draft: 1, Submitted: 0, Approved: 0, Rejected: 0, Snapshots: 0,
            SubmissionDeadlineUtc: null, TimeZoneId: "Asia/Almaty");

    private GetCampaignSummaryHandler Handler()
        => new(_store, _access, _user, new TestClock(new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc)),
            new CampaignProgressPolicy(3));

    private void Profile(AccessBuilder builder)
        => _access.BuildProfileAsync(7, Arg.Any<CancellationToken>()).Returns(builder.Build());
}
