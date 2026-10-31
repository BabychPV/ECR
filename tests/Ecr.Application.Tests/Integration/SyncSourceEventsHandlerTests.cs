// tests/Ecr.Application.Tests/Integration/SyncSourceEventsHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration.SourceEvents;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Abstractions;
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
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();

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

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Directive", "HSE301-EFSYNC")]
    public async Task Підтвердження_масового_видалення_ставить_прапор_у_payload_на_власну_ціль_і_лише_з_правом_Manage()
    {
        Allow("Integration.View");
        await Assert.ThrowsAsync<AccessDeniedException>(() => Sync().HandleAsync(EntityId, default, confirmRemoval: true));
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueCoalescedAsync<ISourceEventSyncJob>(default!, default, default);

        Allow("Integration.Manage");
        await Sync().HandleAsync(EntityId, default, confirmRemoval: true);

        await _jobs.Received(1).EnqueueCoalescedAsync<ISourceEventSyncJob>(
            "source-events-e5-confirm",
            Arg.Is<object?>(p => p is SourceEventSyncRequest && ((SourceEventSyncRequest)p).ConfirmRemoval),
            Arg.Any<CancellationToken>(),
            Actor);

        // Звичайна постановка прапора не несе (розклад і збір його не ставлять).
        await Sync().HandleAsync(EntityId, default);
        await _jobs.Received(1).EnqueueCoalescedAsync<ISourceEventSyncJob>(
            "source-events-e5",
            Arg.Is<object?>(p => p is SourceEventSyncRequest && !((SourceEventSyncRequest)p).ConfirmRemoval),
            Arg.Any<CancellationToken>(),
            Actor);
    }

    /// <summary>
    /// D-464: підтвердження масового видалення лишає слід у журналі структурних змін (хто, яка сутність, яка задача),
    /// звичайна синхронізація — ні; відмова в праві — ні постановки, ні сліду.
    /// </summary>
    /// <remarks>Мутація: прибрати запис журналу в <c>SyncSourceEventsHandler</c> — тест червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Decision", "D-464")]
    public async Task D464_підтвердження_видалення_пишеться_в_журнал_структурних_змін_а_звичайний_синк_ні()
    {
        var now = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        _clock.UtcNow.Returns(now);
        _user.CorrelationId.Returns("corr-1");

        await Sync().HandleAsync(EntityId, default);
        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);

        await Sync().HandleAsync(EntityId, default, confirmRemoval: true);

        await _audit.Received(1).WriteStructureChangeAsync(
            Arg.Is<StructureChangeRecord>(r =>
                r.EntityType == "ext.SourceEntity"
                && r.EntityId == EntityId
                && r.Operation == SyncSourceEventsHandler.RemovalConfirmedOperation
                && r.ChangedByUserId == Actor
                && r.ChangedAt == now
                && r.CorrelationId == "corr-1"
                && r.NewJson!.Contains("job-1", StringComparison.Ordinal)
                && r.ChangeReason!.Contains("integrationAudit.sourceEventRemovalConfirmed", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        _audit.ClearReceivedCalls();
        Allow("Integration.View");
        await Assert.ThrowsAsync<AccessDeniedException>(() => Sync().HandleAsync(EntityId, default, confirmRemoval: true));
        await _audit.DidNotReceiveWithAnyArgs().WriteStructureChangeAsync(default!, default);
    }

    private SyncSourceEventsHandler Sync() => new(_sources, _maps, _jobs, _access, _user, _audit, _clock);

    private void Allow(string permission)
        => _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }.Permission(permission).Build());
}
