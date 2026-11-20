// tests/Ecr.Infrastructure.Tests/Persistence/CalculationRunPurgeConcurrencyTests.cs
using System.Data.Common;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>D-560</c>: читачі, що читають прогони й рядки КІЛЬКОМА запитами, не губляться, коли між запитами інший прогін
/// стає актуальним і швидкий шлях прибирає витіснений (M1); перемикання не робить актуальним прогін, закритий як
/// покинутий (M2); <c>Failed</c> прибирається за будь-якою віссю актуальності, але без актуального лишається (LOW-a);
/// висячі посилання на прибрані прогони не ламають читачів (LOW-c).
/// </summary>
/// <remarks>
/// Мутації: прибрати перечитування прогонів у <c>ChangedResultsAsync</c> / <c>ReadYearAsync</c> /
/// <c>CurrentRunSelection.ApprovedAttributionAsync</c> — відповідний тест червоніє; прибрати перевірку статусу під
/// <c>UPDLOCK</c> у <c>SwitchCurrentRunAsync</c> — червоніє тест «закритий як покинутий».
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationRunPurgeConcurrencyTests(SqlServerFixture sql)
{
    private static readonly DateTime T0 = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Порівняння_змін_перечитує_прогони_якщо_актуальний_замінено_й_прибрано_між_запитами()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202601);
        await using var setup = f.Builder.CreateContext();
        var a = await PurgeKit.AddRunAsync(setup, f, "Running", T0);
        await PurgeKit.InsertRowsAsync(setup, f, a, 3, 0, 0, "X");
        await CompleteAsync(f, a);
        var fresh = await PurgeKit.AddRunAsync(setup, f, "Running", T0.AddMinutes(1));
        await PurgeKit.InsertRowsAsync(setup, f, fresh, 3, 0, 0, "X");

        var interceptor = new BeforeResultsInterceptor(async () =>
        {
            await using var db = f.Builder.CreateContext();
            var b = await PurgeKit.AddRunAsync(db, f, "Running", T0.AddMinutes(2));
            await PurgeKit.InsertRowsAsync(db, f, b, 3, 0, 0, "X");
            await CompleteAsync(f, b);
        });
        await using var reader = sql.CreateContext(interceptor);

        var changed = await new CalculationResultStore(reader, new TestClock(T0))
            .ChangedResultsAsync(fresh, f.Document.DocumentId, [f.MethodologyId], CancellationToken.None);

        Assert.True(interceptor.Fired, "гонитву не відтворено");

        // Ті самі числа, що й в актуальному (новому) прогоні: змін немає. Без перечитування — порівняння з прибраним
        // прогоном дало б хибне «змінено».
        Assert.Empty(changed);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Огляд_року_перечитує_прогони_якщо_актуальний_замінено_й_прибрано_між_запитами()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202602);
        await using var setup = f.Builder.CreateContext();
        var a = await PurgeKit.AddRunAsync(setup, f, "Running", T0);
        await PurgeKit.InsertRowsAsync(setup, f, a, 3, 0, 0, "A");
        await CompleteAsync(f, a);

        var interceptor = new BeforeResultsInterceptor(async () =>
        {
            await using var db = f.Builder.CreateContext();
            var b = await PurgeKit.AddRunAsync(db, f, "Running", T0.AddMinutes(2));
            await PurgeKit.InsertRowsAsync(db, f, b, 3, 0, 0, "B");
            await CompleteAsync(f, b);
        });
        await using var reader = sql.CreateContext(interceptor);

        var facts = await new CalculationResultStore(reader, new TestClock(T0)).ReadYearAsync(
            new CalculationYearReadRequest(
                f.Document.DocumentId, [f.Period], [], [], IncludeRows: false, SliceResults: false, Aggregate: false),
            CancellationToken.None);

        Assert.True(interceptor.Fired, "гонитву не відтворено");
        Assert.Equal(3, facts.Results.Count);
        Assert.All(facts.Results, r => Assert.Equal("B", r.OutputCode));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Атрибуція_затверджених_екземплярів_перечитує_прогони_між_запитами()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202603);
        await using var setup = f.Builder.CreateContext();
        var a = await PurgeKit.AddRunAsync(setup, f, "Running", T0);
        await PurgeKit.InsertRowsAsync(setup, f, a, 2, 0, 0, "A");
        await SetInstanceAsync(setup, a, f.Document.TableInstanceId);
        await CompleteAsync(f, a);

        var interceptor = new BeforeResultsInterceptor(async () =>
        {
            await using var db = f.Builder.CreateContext();
            var b = await PurgeKit.AddRunAsync(db, f, "Running", T0.AddMinutes(2));
            await PurgeKit.InsertRowsAsync(db, f, b, 2, 0, 0, "B");
            await SetInstanceAsync(db, b, f.Document.TableInstanceId);
            await CompleteAsync(f, b);
        });
        await using var reader = sql.CreateContext(interceptor);

        var (attributed, legacy) = await CurrentRunSelection.ApprovedAttributionAsync(
            reader, f.Document.DocumentId, [f.Period], [f.Document.TableInstanceId], CancellationToken.None);

        Assert.True(interceptor.Fired, "гонитву не відтворено");
        Assert.Contains(f.Document.TableInstanceId, attributed);
        Assert.Empty(legacy);
    }

    /// <summary>
    /// M2: <c>AbandonedWorkSweeper</c> закрив живий прогін як <c>Failed</c> (прибирання вже могло прибрати його рядки);
    /// прогін, завершившись, НЕ стає актуальним — попередній лишається актуальним із повними рядками.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_закритий_як_покинутий_не_стає_актуальним_після_завершення()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202604);
        await using var setup = f.Builder.CreateContext();
        var a = await PurgeKit.AddRunAsync(setup, f, "Running", T0);
        await PurgeKit.InsertRowsAsync(setup, f, a, 3, 1, 1, "A");
        await CompleteAsync(f, a);

        var revived = await PurgeKit.AddRunAsync(setup, f, "Running", T0.AddMinutes(1));
        await PurgeKit.InsertRowsAsync(setup, f, revived, 3, 1, 1, "R");

        // Прибиральник покинутої роботи: «досі Running» → Failed.
        await setup.CalculationRuns.Where(r => r.Id == revived && r.Status == "Running")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CompleteAsync(f, revived));

        await using var check = f.Builder.CreateContext();
        var statuses = await check.CalculationRuns.AsNoTracking()
            .Where(r => r.Id == a || r.Id == revived)
            .ToDictionaryAsync(r => r.Id, r => r.Status);
        Assert.Equal(CalculationRun.CurrentStatus, statuses[a]);
        Assert.Equal("Failed", statuses[revived]);
        Assert.Equal((3, 1, 1), await PurgeKit.CountsAsync(check, a));
    }

    /// <summary>
    /// LOW-a: <c>Failed</c> документного прогону, старіший за актуальний прогін УСЬОГО проєкту того самого періоду,
    /// прибирається (за будь-якою віссю); <c>Failed</c>, для якого актуального немає зовсім, лишається (єдина
    /// діагностика).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Failed_прибирається_за_проєктним_актуальним_а_без_актуального_лишається()
    {
        var covered = await PurgeKit.ArrangeAsync(sql, 202605);
        await using var db = covered.Builder.CreateContext();
        var failedDoc = await PurgeKit.AddRunAsync(db, covered, "Failed", T0);
        await PurgeKit.InsertRowsAsync(db, covered, failedDoc, 2, 2, 2, "F");
        await PurgeKit.AddRunAsync(db, covered, "Current", T0.AddHours(1), documentScoped: false);

        var lonely = await PurgeKit.ArrangeAsync(sql, 202605);
        var failedAlone = await PurgeKit.AddRunAsync(db, lonely, "Failed", T0);
        await PurgeKit.InsertRowsAsync(db, lonely, failedAlone, 2, 2, 2, "L");

        var store = new CalculationResultStore(db, new TestClock(T0.AddDays(2)), purgeOptions: PurgeKit.On());
        await store.PurgeSupersededRunsAsync(
            [new SupersededRun(failedDoc, covered.Period, covered.Document.DocumentId),
             new SupersededRun(failedAlone, lonely.Period, lonely.Document.DocumentId)],
            CancellationToken.None);

        Assert.False(await PurgeKit.RunExistsAsync(db, failedDoc));
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(db, failedDoc));
        Assert.True(await PurgeKit.RunExistsAsync(db, failedAlone));
        Assert.Equal((2, 2, 2), await PurgeKit.CountsAsync(db, failedAlone));
    }

    /// <summary>LOW-c: проблема розрахунку, що посилається на прибраний прогін (FK немає), читається як і раніше.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Проблеми_розрахунку_з_посиланням_на_прибраний_прогін_читаються()
    {
        var f = await PurgeKit.ArrangeAsync(sql, 202606);
        await using var db = f.Builder.CreateContext();
        var old = await PurgeKit.AddRunAsync(db, f, "Superseded", T0);
        await PurgeKit.InsertRowsAsync(db, f, old, 1, 1, 1, "O");
        db.CalculationIssues.Add(new CalculationIssue(
            f.Document.DocumentId, f.Period, f.Document.TableDefId, null, "R-1", 1, f.MethodologyId, old, T0,
            ICalculationIssueStore.RowInactive));
        await db.SaveChangesAsync();

        await new CalculationResultStore(db, new TestClock(T0), purgeOptions: PurgeKit.On())
            .PurgeSupersededRunsAsync([new SupersededRun(old, f.Period, f.Document.DocumentId)], CancellationToken.None);
        Assert.False(await PurgeKit.RunExistsAsync(db, old));

        var issues = await new CalculationIssueStore(db, new TestClock(T0))
            .ListAsync(f.Document.DocumentId, f.Period, null, CancellationToken.None);
        Assert.Single(issues);
    }

    private static Task<int> SetInstanceAsync(EcrDbContext db, long runId, long instanceId)
        => db.Database.ExecuteSqlAsync(
            $"UPDATE calc.CalculationResult SET TableInstanceId = {instanceId} WHERE CalculationRunId = {runId}");

    private static async Task CompleteAsync(PurgeStand f, long runId)
    {
        await using var db = f.Builder.CreateContext();
        var clock = new TestClock(T0);
        var handler = new RunCalculationHandler(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock, purgeOptions: PurgeKit.On()),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IAuditWriter>());

        await handler.CompleteAsync(runId, new ModuleProfile(), CancellationToken.None);
    }

    /// <summary>Перед першим запитом до результатів виконує дію (раз): гонитва між запитами читача.</summary>
    private sealed class BeforeResultsInterceptor(Func<Task> action) : DbCommandInterceptor
    {
        private int fired;

        public bool Fired => Volatile.Read(ref fired) == 1;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("CalculationResult", StringComparison.Ordinal)
                && Interlocked.Exchange(ref fired, 1) == 0)
            {
                await action().ConfigureAwait(false);
            }

            return result;
        }
    }
}
