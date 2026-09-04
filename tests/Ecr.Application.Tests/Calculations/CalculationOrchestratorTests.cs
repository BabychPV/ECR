// tests/Ecr.Application.Tests/Calculations/CalculationOrchestratorTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using NSubstitute;
using Xunit;

namespace Ecr.Application.Tests.Calculations;

/// <summary>
/// Оркестрація прогону. Профіль по модулях заповнюється **завжди**: без нього
/// невідомо, звідки брати різницю між 20 і 10 хвилинами (ПРД-13, `J-1`).
/// </summary>
public sealed class CalculationOrchestratorTests
{
    private const int Project = 1;
    private const int Period = 202601;
    private const int Runner = 9;
    private const int Approver = 7;

    private static readonly DateTime Now = new(2026, 4, 10, 9, 0, 0, DateTimeKind.Utc);

    private readonly IPeriodStore _periods = Substitute.For<IPeriodStore>();
    private readonly IWorkflowStore _workflow = Substitute.For<IWorkflowStore>();
    private readonly ICalculationResultStore _results = Substitute.For<ICalculationResultStore>();
    private readonly IBackgroundJobScheduler _jobs = Substitute.For<IBackgroundJobScheduler>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public CalculationOrchestratorTests()
    {
        _clock.UtcNow.Returns(Now);
        _user.UserId.Returns(Runner);

        States(PeriodState.Open);
        _workflow.HasSubmittedSheetsAsync(Project, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(false);
        _jobs.EnqueueAsync<IRecalculationJob>(Arg.Any<object?>(), Arg.Any<CancellationToken>())
             .Returns("job-1");
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Незалежні_гілки_графа_рахуються_паралельно()
    {
        // Дві незалежні гілки: 10 → 11 і 20 → 21. Плюс 30, що не залежить ні
        // від чого і ні від кого — саме такий вузол найлегше загубити.
        var levels = CalculationPlan.Build(
        [
            new CalculationNode(11, [10]),
            new CalculationNode(10, []),
            new CalculationNode(21, [20]),
            new CalculationNode(20, []),
            new CalculationNode(30, []),
        ]);

        // ⚠ Незалежні гілки лягають в ОДИН рівень — саме це й дозволяє
        // рахувати їх одночасно. Послідовний прогін у бюджет 10 хвилин не
        // вкладається (ПРД-13): базова лінія чинної системи — 20, тож
        // «не гірше» тут не працює.
        Assert.Equal(2, levels.Count);
        Assert.Equal([10, 20, 30], levels[0].MethodologyVersionIds);
        Assert.Equal([11, 21], levels[1].MethodologyVersionIds);

        // ⛔ Цикл — відмова, а не «порахуємо як вийде»: порядок навмання дав
        // би числа, які змінюються між прогонами.
        var error = Assert.Throws<BusinessRuleException>(() => CalculationPlan.Build(
        [
            new CalculationNode(1, [2]),
            new CalculationNode(2, [1]),
        ]));

        Assert.Equal("ECR-TMPL-4221", error.ErrorCode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Профіль_по_модулях_заповнюється()
    {
        var profile = new ModuleProfile();
        profile.Record("generic", TimeSpan.FromMilliseconds(1200), rows: 5000);
        profile.Record("generic", TimeSpan.FromMilliseconds(300), rows: 1000);
        profile.Record("flare", TimeSpan.FromMilliseconds(80), rows: 12);

        // Виклики того самого модуля сумуються — інакше «топ-5 модулів» показував
        // би найдовший ОДИН виклик, а не найдорожчий модуль.
        var generic = profile.Stats[0];
        Assert.Equal("generic", generic.Code);
        Assert.Equal(1500, generic.Elapsed.TotalMilliseconds);
        Assert.Equal(6000, generic.Rows);
        Assert.Equal(2, generic.Calls);

        // Порядок від найповільнішого: файл читає людина, і перший рядок має
        // відповідати на питання «куди пішов час» (J-1).
        Assert.Equal(["generic", "flare"], profile.Stats.Select(s => s.Code));

        await Handler().CompleteAsync(77, profile, CancellationToken.None);

        // ⚠ Профіль пишеться ЗАВЖДИ, а не лише в діагностичному режимі: бюджет
        // 10 хвилин — вимога, і прогін без профілю нічого не каже про те,
        // звідки брати різницю з двадцятьма.
        await _results.Received(1).SwitchCurrentRunAsync(
            77, Arg.Is<string>(json => json.Contains("generic", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        // Навіть порожній профіль пишеться: «нічого не зміряли» — теж факт.
        await Handler().CompleteAsync(78, new ModuleProfile(), CancellationToken.None);
        await _results.Received(1).SwitchCurrentRunAsync(
            78, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Закритий_період_не_перераховується_автоматично()
    {
        States(PeriodState.Closed);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Project, Period, approval: null, CancellationToken.None));

        // ⛔ Перерахунок закритого періоду змінює числа, які вже подані
        // регулятору, і робить це без жодного сліду в самих даних (ФВ-9.7).
        Assert.Equal("ECR-CALC-4221", error.ErrorCode);
        await _jobs.DidNotReceive().EnqueueAsync<IRecalculationJob>(
            Arg.Any<object?>(), Arg.Any<CancellationToken>());

        // З погодженням від іншої людини і з причиною — проходить. Погодження
        // саме окреме: прапорець у запиті звівся б до зайвого поля у формі.
        var jobId = await Handler().HandleAsync(
            Project, Period, new ClosedPeriodApproval(Approver, "Помилка коефіцієнта, лист №17"),
            CancellationToken.None);

        Assert.Equal("job-1", jobId);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task Поданий_зріз_не_перераховується_взагалі()
    {
        _workflow.HasSubmittedSheetsAsync(Project, Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
                 .Returns(true);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Handler().HandleAsync(Project, Period, approval: null, CancellationToken.None));

        // ⛔ Період ВІДКРИТИЙ — і все одно відмова (ФВ-9.17). Потреба змінити
        // подану цифру закривається Reopen, який лишає слід у робочому процесі,
        // а не тихим перерахунком, після якого поданий зріз і поточні дані
        // розходяться без жодної позначки.
        Assert.Equal("ECR-CALC-4221", error.ErrorCode);
        await _jobs.DidNotReceive().EnqueueAsync<IRecalculationJob>(
            Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public async Task IsCurrent_перемикається_однією_транзакцією()
    {
        var profile = new ModuleProfile();
        profile.Record("generic", TimeSpan.FromSeconds(1), rows: 10);

        await Handler().CompleteAsync(77, profile, CancellationToken.None);

        // ⚠ Рівно ОДИН виклик сховища на перемикання. Між зняттям актуальності
        // зі старого прогону і встановленням новому існує стан, у якому
        // актуальних прогонів нуль або два, і звіт, побудований у цю мить, не
        // має правильної відповіді (ФВ-9.11).
        await _results.Received(1).SwitchCurrentRunAsync(
            77, Arg.Any<string>(), Arg.Any<CancellationToken>());

        // І один коміт на все завершення: профіль, перемикання й інвалідація
        // зрізів — одна транзакція, а не три.
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _results.Received(1).InvalidateReportSnapshotsAsync(77, Arg.Any<CancellationToken>());
    }

    private RunCalculationHandler Handler()
        => new(_periods, _workflow, _results, _jobs, _uow, _user, _clock);

    private void States(PeriodState state)
        => _periods.GetPeriodStatesAsync(Project, Period, Arg.Any<CancellationToken>())
                   .Returns(new List<PeriodStateRef> { new(Period, state) });
}
