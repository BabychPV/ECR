// tests/Ecr.Application.Tests/Sources/RowWindowMapEnqueueTransactionTests.cs
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Security;
using Ecr.Application.Sources;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Sources;

/// <summary>
/// V6-02 (MI-02 (в)): постановка перечитування рядків після створення/правки прив'язки вікна рядка іде в
/// ТРАНЗАКЦІЇ, коли черга в базі, і після коміту, коли Quartz у пам'яті.
/// </summary>
/// <remarks>
/// Доти постановка йшла після коміту завжди: зупинка процесу чи збій черги між комітом і постановкою лишали
/// прив'язку (а для нової - без жодного провенансу, який підібрав би повтор) без перечитування. Мутація: прибрати
/// гілку <c>enlist</c> - у режимі бази постановка стає поза транзакцією, перший рядок теорії червоний.
/// </remarks>
public sealed class RowWindowMapEnqueueTransactionTests
{
    private const int Table = 10;
    private const int MapId = 77;
    private const int UserId = 9;

    private readonly IRowWindowMapStore _store = Substitute.For<IRowWindowMapStore>();
    private readonly ICollectionStore _sources = Substitute.For<ICollectionStore>();
    private readonly IAccessDecisionService _access = Substitute.For<IAccessDecisionService>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly List<string> _enqueues = [];
    private bool _inTransaction;

    public RowWindowMapEnqueueTransactionTests()
    {
        _user.UserId.Returns(UserId);
        _access.BuildProfileAsync(UserId, Arg.Any<CancellationToken>())
            .Returns(new AccessBuilder { UserId = UserId }.Permission("Integration.Manage").Build());
        _sources.FindProjectIdsUsingColumnAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        _sources.UnitExistsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        var columns = new Dictionary<int, ColumnDef>
        {
            [3] = Column("VOLUME", CellDataType.Decimal, 3),
            [1] = Column("START_AT", CellDataType.Date, 1),
            [2] = Column("END_AT", CellDataType.Date, 2),
        };
        _store.FindColumnsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(columns);
        _store.OpenInstancesAsync(Table, Arg.Any<long?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<RowWindowFetchRequest>>(
                call.ArgAt<long?>(1) is null ? [new RowWindowFetchRequest(100, 202601)] : []));
        _store.AddMapAsync(Arg.Any<RowWindowMap>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<RowWindowMap>()));

        _uow.ExecuteInTransactionAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                _inTransaction = true;
                try
                {
                    await call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>());
                }
                finally
                {
                    _inTransaction = false;
                }
            });

        _jobs.EnqueueCoalescedAsync<IRowWindowFetchJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns(_ =>
            {
                _enqueues.Add(_inTransaction ? "in-transaction" : "after-commit");
                return Task.FromResult("job");
            });
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "V6-02")]
    [InlineData(true, "in-transaction")]
    [InlineData(false, "after-commit")]
    public async Task V6_02_правка_прив_язки_ставить_перечитування_в_транзакції_лише_для_черги_в_базі(
        bool enlists, string expected)
    {
        _jobs.EnlistsInCallerTransaction.Returns(enlists);
        _store.FindMapAsync(MapId, Arg.Any<CancellationToken>()).Returns(Map());

        await UpdateHandler().HandleAsync(MapId, UpdateCommand(), CancellationToken.None);

        Assert.Equal([expected], _enqueues);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "V6-02")]
    [InlineData(true, "in-transaction")]
    [InlineData(false, "after-commit")]
    public async Task V6_02_створення_прив_язки_ставить_перечитування_в_транзакції_лише_для_черги_в_базі(
        bool enlists, string expected)
    {
        _jobs.EnlistsInCallerTransaction.Returns(enlists);
        _store.TargetTakenAsync(Table, 3, Arg.Any<CancellationToken>()).Returns(false);

        var handler = new CreateRowWindowMapHandler(
            _store, _sources, _access, _user, _uow, Substitute.For<IAuditWriter>(), Substitute.For<IClock>(),
            Substitute.For<IRowWindowColumnIndex>(), _jobs);

        await handler.HandleAsync(
            new CreateRowWindowMapCommand(Table, 3, 1, 2, null, RowWindowSummaryKind.Total, false, 501, null, null, null, []),
            CancellationToken.None);

        Assert.Equal([expected], _enqueues);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Finding", "V6-02")]
    public async Task V6_02_черга_в_базі_збій_постановки_виходить_з_транзакції_а_не_лишає_правку_закоміченою()
    {
        _jobs.EnlistsInCallerTransaction.Returns(true);
        _jobs.EnqueueCoalescedAsync<IRowWindowFetchJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns<string>(_ => throw new InvalidOperationException("itg.JobQueue"));
        _store.FindMapAsync(MapId, Arg.Any<CancellationToken>()).Returns(Map());

        // Виняток із тіла транзакції виходить з обробника: у справжньому UnitOfWork це відкат усієї правки.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UpdateHandler().HandleAsync(MapId, UpdateCommand(), CancellationToken.None));
    }

    private UpdateRowWindowMapHandler UpdateHandler()
        => new(
            _store, _sources, _access, _user, _uow, Substitute.For<IAuditWriter>(), Substitute.For<IClock>(),
            Substitute.For<IRowWindowColumnIndex>(), _jobs);

    private static UpdateRowWindowMapCommand UpdateCommand()
        => new(1, 2, null, RowWindowSummaryKind.Total, false, 501, null, null, null, true, null, []);

    private static RowWindowMap Map()
    {
        var map = RowWindowMap.Create(
            Column("VOLUME", CellDataType.Decimal, 3),
            Column("START_AT", CellDataType.Date, 1),
            Column("END_AT", CellDataType.Date, 2),
            selector: null,
            RowWindowSummaryKind.Total,
            isStep: false,
            targetUnitId: 501);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(map, MapId);
        return map;
    }

    private static ColumnDef Column(string code, CellDataType type, int id)
    {
        var column = new ColumnDef(
            Table, EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), id, type);
        typeof(Entity<int>).GetProperty(nameof(Entity<int>.Id))!.SetValue(column, id);
        return column;
    }
}
