using Ecr.Application.Integration;
using Ecr.Application.Ports;
using Ecr.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// Хук запису комірок для вікон рядків (HSE301 A1, §4.4): ставить підтягування лише за правкою Початку/Кінця/селектора.
/// </summary>
/// <remarks>
/// ⛔ Мутаційні докази: приберіть перевірку <c>ChangedColumnDefIds.Any(windowColumns.Contains)</c> — червоніє
/// <see cref="Правка_чужої_колонки_нічого_не_ставить_у_чергу"/>; замініть <c>EnqueueCoalescedAsync</c> на
/// <c>EnqueueExclusiveAsync</c> — червоніє <see cref="Правка_Початку_Кінця_чи_селектора_ставить_задачу_на_екземпляр_без_витіснення"/>.
/// </remarks>
public sealed class RowWindowTriggerTests
{
    private const int TableDefId = 7;
    private const int StartColumn = 11;
    private const int EndColumn = 12;
    private const int SelectorColumn = 13;
    private const int OtherColumn = 99;

    private readonly IRowWindowColumnIndex _index = Substitute.For<IRowWindowColumnIndex>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();

    private RowWindowTrigger Trigger()
    {
        _index.WindowColumnsAsync(TableDefId, Arg.Any<CancellationToken>())
            .Returns(new HashSet<int> { StartColumn, EndColumn, SelectorColumn });
        _index.WindowColumnsAsync(Arg.Is<int>(id => id != TableDefId), Arg.Any<CancellationToken>())
            .Returns(new HashSet<int>());

        return new RowWindowTrigger(_index, _jobs, NullLogger<RowWindowTrigger>.Instance);
    }

    private static RowWindowChange Change(int tableDefId, params int[] columns)
        => new(TableInstanceId: 100, PeriodKey: 202601, tableDefId, columns);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    [InlineData(StartColumn)]
    [InlineData(EndColumn)]
    [InlineData(SelectorColumn)]
    public async Task Правка_Початку_Кінця_чи_селектора_ставить_задачу_на_екземпляр_без_витіснення(int column)
    {
        await Trigger().RowsChangedAsync(Change(TableDefId, OtherColumn, column), CancellationToken.None);

        await _jobs.Received(1).EnqueueCoalescedAsync<IRowWindowFetchJob>(
            RowWindowFetchTarget.Of(100), Arg.Any<object?>(), Arg.Any<CancellationToken>(), null);
        var payload = Assert.Single(_jobs.ReceivedCalls()).GetArguments()[1];
        Assert.Equal(new RowWindowFetchRequest(100, 202601), payload);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<IRowWindowFetchJob>(default!, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public async Task Правка_чужої_колонки_нічого_не_ставить_у_чергу()
    {
        await Trigger().RowsChangedAsync(Change(TableDefId, OtherColumn), CancellationToken.None);

        Assert.Empty(_jobs.ReceivedCalls());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public async Task Таблиця_без_прив_язок_не_питає_чергу_і_порожня_правка_не_питає_знімок()
    {
        var trigger = Trigger();

        await trigger.RowsChangedAsync(Change(TableDefId + 1, StartColumn), CancellationToken.None);
        await trigger.RowsChangedAsync(Change(TableDefId), CancellationToken.None);

        Assert.Empty(_jobs.ReceivedCalls());
        await _index.DidNotReceive().WindowColumnsAsync(TableDefId, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait("Directive", "HSE301-A1")]
    public async Task Збій_черги_не_валить_запис_комірок()
    {
        _jobs.EnqueueCoalescedAsync<IRowWindowFetchJob>(default!, default, default, default)
            .ReturnsForAnyArgs<Task<string>>(_ => throw new InvalidOperationException("queue down"));

        await Trigger().RowsChangedAsync(Change(TableDefId, StartColumn), CancellationToken.None);
    }
}
