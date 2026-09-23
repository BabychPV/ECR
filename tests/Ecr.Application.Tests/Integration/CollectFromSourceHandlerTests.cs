// tests/Ecr.Application.Tests/Integration/CollectFromSourceHandlerTests.cs
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.Errors;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Запуск збору (<see cref="CollectFromSourceHandler"/>): існування сутності
/// джерела перевіряється ДО постановки задачі в чергу (коментар у
/// <c>IntegrationHandlers.cs</c>) — інакше задача провалилась би у фоні через
/// хвилину, і причину шукали б не там.
/// </summary>
public sealed class CollectFromSourceHandlerTests
{
    private const int Actor = 5;
    private const int SourceEntityId = 77;

    private readonly ICollectionStore _sources = Substitute.For<ICollectionStore>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    public CollectFromSourceHandlerTests()
    {
        _user.UserId.Returns(Actor);
        _access.BuildProfileAsync(Actor, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = Actor }
                .Permission(CollectFromSourceHandler.Permission).Build());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Наявна_сутність_ставить_задачу_в_чергу_з_переданим_діапазоном()
    {
        var entity = new SourceEntity(dataSourceId: 3, code: "ENT-77", RegistrySourceKind.External);
        _sources.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>()).Returns(entity);
        _jobs.EnqueueAsync<ICollectionJob>(Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns("job-123");

        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);

        var jobId = await Handler().HandleAsync(SourceEntityId, from, to, CancellationToken.None);

        Assert.Equal("job-123", jobId);

        // ⛔ Головне твердження: діапазон і ціль доходять до планувальника
        // саме такими, якими їх передали, — не заново обчисленими.
        await _jobs.Received(1).EnqueueAsync<ICollectionJob>(
            Arg.Is<object?>(p => p is CollectionTask
                && ((CollectionTask)p!).SourceEntityId == SourceEntityId
                && ((CollectionTask)p!).FromUtc == from
                && ((CollectionTask)p!).ToUtc == to),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public async Task Неіснуюча_або_вимкнена_сутність_дає_404_і_не_ставить_задачу()
    {
        _sources.FindSourceEntityAsync(SourceEntityId, Arg.Any<CancellationToken>())
            .Returns((SourceEntity?)null);

        var missing = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(SourceEntityId, null, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.SourceEntityNotFound, missing.ErrorCode);
        Assert.Equal("err.ECR-INT-0404.sourceEntity", missing.Details!["messageKey"]);
        Assert.Equal(
            SourceEntityId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            missing.Details!["id"]);

        // ⛔ Існування перевіряється ДО постановки в чергу: задача на
        // неіснуючу сутність провалилась би у фоні через хвилину, і причину
        // шукали б не там (коментар в IntegrationHandlers.cs).
        await _jobs.DidNotReceive().EnqueueAsync<ICollectionJob>(
            Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    private CollectFromSourceHandler Handler() => new(_sources, _jobs, _access, _user);
}
