// tests/Ecr.Infrastructure.Tests/Jobs/AwaitingOpenPeriodTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.Services;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ D-575 (рішення людини 23.11, варіант B): формули періоду, який ще не відкрито, не обчислюються — тож (1) сховище
/// періодів має сказати «ще не відкрито» лише для АКТИВНОГО проєкту (у чернетці Scheduled — усі періоди, і формули там
/// рахуються, як доти), а (2) перехід <c>Scheduled → Open</c> ставить перерахунок документів місяця — інакше він
/// лишався б порожнім до першої правки чи нічного прогону.
/// </summary>
[Collection("SqlServer")]
public sealed class AwaitingOpenPeriodTests(SqlServerFixture sql)
{
    /// <summary>Січень ще не відкрито (відкриття — 15 лютого місцевого за політикою OPN45).</summary>
    private static readonly DateTime BeforeOpen = new(2026, 1, 25, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>Задача станів: 20 лютого січень уже відкрито.</summary>
    private static readonly DateTime StateRun = new(2026, 2, 20, 6, 0, 0, DateTimeKind.Utc);

    private static PeriodPolicy Policy()
        => new(EcrCode.Create("OPN45"), openOffsetDays: 45, graceOffsetDays: 30,
            hardCloseOffsetDays: 60, yearGraceOffsetDays: 45);

    /// <remarks>
    /// ⛔ Мутація (локально, не комітити): у <c>PeriodStore.FindEvaluationStateAsync</c> повернути
    /// <c>AwaitingOpen = false</c> завжди — перший Assert червоний; прибрати умову «активний проєкт» — червоний останній.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-575")]
    public async Task Ще_не_відкрито_лише_Scheduled_активного_проєкту()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);
        var draft = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        try
        {
            await ArmPeriodAsync(db, chain);

            var before = await new PeriodStore(db, new TestClock(BeforeOpen))
                .FindEvaluationStateAsync(chain.DocumentId, chain.PeriodKey.Value, CancellationToken.None);
            Assert.Equal(new PeriodEvaluationState(PeriodState.Scheduled, AwaitingOpen: true), before);

            // Ефективний стан: межа відкриття минула — період відкритий і без прогону задачі станів.
            var after = await new PeriodStore(db, new TestClock(StateRun))
                .FindEvaluationStateAsync(chain.DocumentId, chain.PeriodKey.Value, CancellationToken.None);
            Assert.Equal(new PeriodEvaluationState(PeriodState.Open, AwaitingOpen: false), after);

            // Той самий стан і через старий вхід (одне правило).
            Assert.Equal(
                PeriodState.Scheduled,
                await new PeriodStore(db, new TestClock(BeforeOpen))
                    .FindPeriodStateAsync(chain.DocumentId, chain.PeriodKey.Value, CancellationToken.None));

            // Чернетка: Scheduled за побудовою, формули рахуються (D-575 не діє).
            var inDraft = await new PeriodStore(db, new TestClock(BeforeOpen))
                .FindEvaluationStateAsync(draft.DocumentId, draft.PeriodKey.Value, CancellationToken.None);
            Assert.Equal(new PeriodEvaluationState(PeriodState.Scheduled, AwaitingOpen: false), inDraft);
        }
        finally
        {
            await RetireAsync(builder, chain);
        }
    }

    /// <remarks>
    /// ⛔ Мутація (локально, не комітити): прибрати виклик <c>openedRecalculation.RequestAsync</c> у <c>PeriodStateJob</c>
    /// — перерахунку немає, тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-575")]
    public async Task Відкриття_періоду_ставить_перерахунок_документів_місяця_один_раз()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        try
        {
            await ArmPeriodAsync(db, chain);
            var trigger = Substitute.For<ICalculationTrigger>();

            await TransitionAsync(db, trigger, StateRun);

            db.ChangeTracker.Clear();
            Assert.Equal(
                PeriodState.Open,
                await db.Periods
                    .Where(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value)
                    .Select(p => p.State)
                    .SingleAsync());
            await trigger.Received(1).RequestAsync(chain.DocumentId, chain.PeriodKey, Arg.Any<CancellationToken>());

            // Повторний прогін: переходу немає — і перерахунку теж.
            await TransitionAsync(db, trigger, StateRun.AddHours(1));
            await trigger.Received(1).RequestAsync(chain.DocumentId, chain.PeriodKey, Arg.Any<CancellationToken>());
        }
        finally
        {
            await RetireAsync(builder, chain);
        }
    }

    /// <remarks>
    /// ⛔ Мутація (локально, не комітити): прибрати виклик <c>DropAwaitingOpenErrorsAsync</c> у <c>PeriodStateJob</c> —
    /// перший Assert червоний (помилка не відкритого січня лишається).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "D-575")]
    public async Task Задача_станів_знімає_застарілі_помилки_не_відкритого_періоду_і_лишає_відкритого()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None);

        await using var db = builder.CreateContext();
        try
        {
            await ArmPeriodAsync(db, chain);
            var trigger = Substitute.For<ICalculationTrigger>();

            // Застаріла помилка (прогін до D-575) у ще не відкритому січні, значення комірки — теж є.
            await AddErrorAsync(db, chain);
            await TransitionAsync(db, trigger, BeforeOpen);

            db.ChangeTracker.Clear();
            Assert.Equal(0, await ErrorsAsync(db, chain));
            await trigger.DidNotReceiveWithAnyArgs().RequestAsync(default, default, default);

            // Відкритий місяць: помилка — чинна (D-506), задача станів її не чіпає.
            await TransitionAsync(db, trigger, StateRun);
            await AddErrorAsync(db, chain);
            await TransitionAsync(db, trigger, StateRun.AddHours(1));

            db.ChangeTracker.Clear();
            Assert.Equal(1, await ErrorsAsync(db, chain));
        }
        finally
        {
            await using (var cleanup = builder.CreateContext())
            {
                await cleanup.FormulaErrors.Where(e => e.DocumentId == chain.DocumentId).ExecuteDeleteAsync();
            }

            await RetireAsync(builder, chain);
        }
    }

    private static async Task AddErrorAsync(EcrDbContext db, TestDocument chain)
    {
        db.FormulaErrors.Add(new FormulaError(
            chain.DocumentId, chain.PeriodKey.Value, chain.TableInstanceId, chain.TableDefId, sheetCopyId: null,
            chain.RowIds[0], "R1", chain.ColumnDefIds[0], formulaDefId: 1, FormulaError.ErrorKind, "#N/A",
            inputsJson: null, sourcePeriodKey: null, sourceTableDefId: null, sourceRowKey: null, sourceColumnDefId: null,
            BeforeOpen));
        await db.SaveChangesAsync(CancellationToken.None);
    }

    private static Task<int> ErrorsAsync(EcrDbContext db, TestDocument chain)
        => db.FormulaErrors.CountAsync(e => e.DocumentId == chain.DocumentId && e.PeriodKey == chain.PeriodKey.Value);

    private static Task TransitionAsync(EcrDbContext db, ICalculationTrigger trigger, DateTime at)
        => new PeriodStateJob(
                db, new PeriodStateCalculator(), new UnitOfWork(db), new TestClock(at),
                new MaterializationScheduler(db, Substitute.For<IBackgroundJobScheduler>()),
                openedRecalculation: new OpenedPeriodRecalculation(db, trigger))
            .ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);

    /// <summary>Межі періоду за політикою і активний проєкт — щоб задача станів його бачила.</summary>
    private static async Task ArmPeriodAsync(EcrDbContext db, TestDocument chain)
    {
        var project = await db.Projects.SingleAsync(p => p.Id == chain.ProjectId);
        project.Activate(BeforeOpen);

        var period = await db.Periods.SingleAsync(p => p.ProjectId == chain.ProjectId && p.PeriodKeyValue == chain.PeriodKey.Value);
        period.RecomputeBoundaries(Policy(), SiteTimeZone.Create(project.TimeZoneId).ToTimeZoneInfo());

        await db.SaveChangesAsync(CancellationToken.None);
        Assert.Equal(PeriodState.Scheduled, period.State);
    }

    /// <summary>Архівує проєкт тесту: задача станів обходить лише активні (див. <c>MaterializeOnPeriodOpenTests</c>).</summary>
    private static async Task RetireAsync(TestDocumentBuilder builder, TestDocument chain)
    {
        await using var db = builder.CreateContext();
        var project = await db.Projects.SingleAsync(p => p.Id == chain.ProjectId);
        if (project.Status == ProjectStatus.Active)
        {
            project.Archive(StateRun.AddDays(2));
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
