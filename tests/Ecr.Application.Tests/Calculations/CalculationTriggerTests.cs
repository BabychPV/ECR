// tests/Ecr.Application.Tests/Calculations/CalculationTriggerTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Documents;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Автоперерахунок після запису без людини (<see cref="CalculationTrigger"/>, HSE301 A4, V-5 → <c>D-174</c>).
/// </summary>
/// <remarks>
/// ⚠ Тут — правило «куди можна» і форма постановки. Що черга лишає ОДНУ задачу
/// на кілька викликів — доводить справжній планувальник
/// (<c>AutoRecalcAfterMaterializeTests</c>).
/// </remarks>
public sealed class CalculationTriggerTests
{
    private const long DocumentId = 4501;
    private static readonly PeriodKey Period = new(202601);

    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IWorkflowStore _workflow = Substitute.For<IWorkflowStore>();

    public CalculationTriggerTests()
    {
        _jobs.EnqueueExclusiveAsync<IRecalculationJob>(
                Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("IRecalculationJob~auto");
        _workflow.GetSheetsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApprovalState>());
    }

    private CalculationTrigger Trigger() => new(_jobs, _periods, _workflow);

    private void State(PeriodState? state)
        => _periods.FindPeriodStateAsync(DocumentId, Period.Value, Arg.Any<CancellationToken>()).Returns(state);

    [Theory]
    [InlineData(PeriodState.Open)]
    [InlineData(PeriodState.Grace)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "HSE301-A4")]
    public async Task Відкритий_період_ставить_перерахунок_тим_самим_маркером_і_ціллю_що_й_кнопка(PeriodState state)
    {
        State(state);

        var jobId = await Trigger().RequestAsync(DocumentId, Period, CancellationToken.None);

        Assert.Equal("IRecalculationJob~auto", jobId);

        // ⛔ Та сама ціль, що в `RecalculateDocumentHandler` і правці шапки: інша
        // ціль дала б другу живу задачу поруч із ручною — два прогони, що
        // навперегін перемикають актуальність результатів. Автор — `null`:
        // системна задача, кнопку ніхто не натискав.
        await _jobs.Received(1).EnqueueExclusiveAsync<IRecalculationJob>(
            RecalculateDocumentHandler.TargetOf(DocumentId, Period),
            Arg.Any<object?>(), Arg.Any<CancellationToken>(), null);
    }

    [Theory]
    [InlineData(PeriodState.Closed)]
    [InlineData(PeriodState.Scheduled)]
    [InlineData(null)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "HSE301-A4")]
    public async Task Закритий_ще_не_відкритий_чи_невідомий_період_нуль_задач(PeriodState? state)
    {
        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати перевірку `RecalculationWritePolicy` у
        // тригері → `Closed` ставить задачу, яка впаде в `RecalculationJob`
        // і лишить у журналі провалений перерахунок на кожен пізній збір.
        State(state);

        var jobId = await Trigger().RequestAsync(DocumentId, Period, CancellationToken.None);

        Assert.Null(jobId);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<IRecalculationJob>(
            default!, default, default, default);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "HSE301-A4")]
    public async Task Поданий_аркуш_у_відкритому_періоді_нуль_задач()
    {
        State(PeriodState.Open);
        var submitted = new ApprovalState(DocumentId, sheetDefId: 1, Period.Value);
        submitted.Submit(userId: 9, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        _workflow.GetSheetsAsync(DocumentId, Period, Arg.Any<CancellationToken>())
            .Returns(new[] { submitted });

        var jobId = await Trigger().RequestAsync(DocumentId, Period, CancellationToken.None);

        // ⛔ ФВ-9.17: подана цифра не змінюється перерахунком — шлях один, Reopen.
        Assert.Null(jobId);
        await _jobs.DidNotReceiveWithAnyArgs().EnqueueExclusiveAsync<IRecalculationJob>(
            default!, default, default, default);
    }
}
