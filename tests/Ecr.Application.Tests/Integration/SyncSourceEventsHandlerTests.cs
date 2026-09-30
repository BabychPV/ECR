// tests/Ecr.Application.Tests/Integration/SyncSourceEventsHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// «Отримати з PI зараз» (HSE301 A6): право, вимога активного мапінгу й злиття постановок на ціль сутності.
/// </summary>
public sealed class SyncSourceEventsHandlerTests
{
    private const int Actor = 11;
    private const int EntityId = 5;

    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly ICollectionStore _sources = Substitute.For<ICollectionStore>();
    private readonly ISourceEventMapStore _maps = Substitute.For<ISourceEventMapStore>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();

    public SyncSourceEventsHandlerTests()
    {
        _user.UserId.Returns(Actor);
        Allow("Integration.Manage");

        _sources.FindSourceEntityAsync(EntityId, Arg.Any<CancellationToken>())
            .Returns(new SourceEntity(3, "FlareEvent", RegistrySourceKind.External));
        _maps.HasActiveMapAsync(EntityId, Arg.Any<CancellationToken>()).Returns(true);
        _jobs.EnqueueCoalescedAsync<ISourceEventSyncJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("job-1");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Синхронізація_ставить_задачу_через_coalesced_на_ціль_сутності_і_повторне_натискання_та_сама_ціль()
    {
        var first = await Sync().HandleAsync(EntityId, default);
        var second = await Sync().HandleAsync(EntityId, default);

        Assert.Equal(("job-1", "job-1"), (first, second));
        await _jobs.Received(2).EnqueueCoalescedAsync<ISourceEventSyncJob>(
            "source-events-e5",
            Arg.Is<object?>(p => p is SourceEventSyncRequest && ((SourceEventSyncRequest)p).SourceEntityId == EntityId),
            Arg.Any<CancellationToken>(),
            Actor);

        // Постановка «з витісненням» тут заборонена: сплеск натискань не має переривати виконуваний синк.
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<ISourceEventSyncJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-A6")]
    public async Task Синхронізація_без_права_чи_сутності_чи_активного_мапінгу_нічого_не_ставить()
    {
        Allow("Integration.View");
        await Assert.ThrowsAsync<AccessDeniedException>(() => Sync().HandleAsync(EntityId, default));

        Allow("Integration.Manage");
        await Assert.ThrowsAsync<NotFoundException>(() => Sync().HandleAsync(404, default));

        _maps.HasActiveMapAsync(EntityId, Arg.Any<CancellationToken>()).Returns(false);
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Sync().HandleAsync(EntityId, default));
        Assert.Equal("err.ECR-INT-0422.eventSyncNoMap", refused.Details!["messageKey"]);

        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<ISourceEventSyncJob>(default!, default, default);
    }

    private SyncSourceEventsHandler Sync() => new(_sources, _maps, _jobs, _access, _user);

    private void Allow(string permission)
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Build());
}
