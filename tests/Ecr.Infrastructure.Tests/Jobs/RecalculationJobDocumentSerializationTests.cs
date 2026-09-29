// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobDocumentSerializationTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// Річна задача документа (<c>doc{id}-year</c>) і поперіодна (<c>doc{id}-p{period}</c>)
/// мають РІЗНІ ключі цілі, тож черга (MI-02) не заважає їм іти одночасно.
/// Задача мусить сама серіалізуватися за документом.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який доводить тест. Річна задача створює прогони «документ × період»
/// по ходу, а перемикає актуальність УСІХ їх лише в кінці. Поперіодна задача, що
/// стартувала ПІЗНІШЕ й завершилась РАНІШЕ, ставала актуальною з БІЛЬШИМ Id; річна,
/// завершуючись, падала на <c>UX_CalculationRun_Current</c> навіть без жодного
/// збігу в часі: EF шле UPDATE за зростанням ключа, тож «свій (менший Id) →
/// Current» іде раніше за «чужий → Superseded». Порушення обмеження не ретраїться
/// (<c>JobRetryPolicy.IsWorthRetrying</c>), і річна задача позначає <c>Failed</c>
/// прогони ВСІХ своїх періодів. А якби й не падала — перекрила б новіше число
/// старішим, порахованим до поперіодної.
/// <para>
/// ⚠ Детерміновано: річна задача тримається бар'єром усередині оркестратора;
/// поперіодна або дійде до свого оркестратора (одночасне виконання — дефект), або
/// стане в очікування лока документа, яке видно в <c>sys.dm_tran_locks</c>.
/// Жодних «зачекати N мс і подивитися».
/// </para>
/// <para>
/// Мутація: прибрати <c>AcquireDocumentLockAsync</c> з <c>RecalculationJob.ExecuteAsync</c>
/// — тест червоніє: річна задача кидає <c>DbUpdateException</c> на
/// <c>UX_CalculationRun_Current</c> (поперіодна пройшла всередину, поки річна рахувала).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobDocumentSerializationTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Річна_і_поперіодна_задачі_одного_документа_не_виконуються_одночасно()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync();
        var january = document.PeriodKey.Value;

        var yearRunner = new GatedRunner(holdUntilReleased: true);
        var periodRunner = new GatedRunner(holdUntilReleased: false);

        await using var yearDb = builder.CreateContext();
        await using var periodDb = builder.CreateContext();

        var yearJob = Job(yearDb, yearRunner);
        var periodJob = Job(periodDb, periodRunner);

        // Нічна документна задача (P4 фан-аут): увесь рік.
        var yearTask = Task.Run(() => yearJob.ExecuteAsync(
            new RecalculationRequest(document.ProjectId, document.DocumentId, PeriodKey: null, TriggeredByUserId: null),
            NoOpProgress.Instance,
            CancellationToken.None));

        await yearRunner.Entered.Task.WaitAsync(Patience);

        // Кнопка «Перерахувати» того самого документа й періоду — поки річна рахує.
        var periodTask = Task.Run(() => periodJob.ExecuteAsync(
            new RecalculationRequest(document.ProjectId, document.DocumentId, january, TriggeredByUserId: 7),
            NoOpProgress.Instance,
            CancellationToken.None));

        using var stopWatch = new CancellationTokenSource();
        var waiting = WaitUntilSomeoneWaitsOnDocumentLockAsync(document.DocumentId, stopWatch.Token);

        var first = await Task.WhenAny(periodRunner.Entered.Task, waiting, periodTask).WaitAsync(Patience);
        var ranConcurrently = first == periodRunner.Entered.Task
                              || (first == periodTask && periodRunner.Entered.Task.IsCompleted);
        await stopWatch.CancelAsync();

        // Якщо поперіодна пройшла всередину — даємо їй завершитися ДО річної: це
        // той самий порядок, що й у житті (коротка задача після довгої), і він
        // робить наслідок детермінованим. Її виняток — одразу, а не «щось зависло».
        if (ranConcurrently || first == periodTask)
        {
            await periodTask.WaitAsync(Patience);
        }

        yearRunner.Release();
        await yearTask.WaitAsync(Patience);
        await periodTask.WaitAsync(Patience);

        var yearRunId = Assert.Single(yearRunner.RunIds);
        var periodRunId = Assert.Single(periodRunner.RunIds);

        await using var check = builder.CreateContext();
        var current = await check.CalculationRuns
            .AsNoTracking()
            .Where(r => r.DocumentId == document.DocumentId
                        && r.PeriodKey == january
                        && r.Status == CalculationRun.CurrentStatus)
            .Select(r => r.Id)
            .ToListAsync();

        // ⛔ Наслідок: актуальним мусить бути прогін ПІЗНІШОЇ постановки. До фіксу
        // річна (почата раніше) завершувалась останньою й перекривала його.
        Assert.Equal([periodRunId], current);
        Assert.NotEqual(yearRunId, periodRunId);

        // ⛔ Причина: поперіодна не мала права почати рахувати, поки річна тримає документ.
        Assert.False(ranConcurrently, "Поперіодна задача рахувала документ одночасно з річною.");
    }

    /// <summary>Чекає, доки якась сесія стане в чергу за локом документа.</summary>
    private async Task WaitUntilSomeoneWaitsOnDocumentLockAsync(long documentId, CancellationToken ct)
    {
        // Літерал, а не посилання на константу задачі: тест мусить компілюватися
        // й на коді ДО фіксу, щоб показати червоне.
        var resource = FormattableString.Invariant($"ecr:recalc:doc:{documentId}");

        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync(ct);

        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM sys.dm_tran_locks
                WHERE resource_type = 'APPLICATION'
                  AND request_status = 'WAIT'
                  AND resource_description LIKE '%' + @resource + '%'
                """;
            command.Parameters.AddWithValue("@resource", resource);

            if ((int)(await command.ExecuteScalarAsync(ct))! > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
        }
    }

    private static RecalculationJob Job(EcrDbContext db, ICalculationRunner runner)
    {
        var clock = new TestClock(Now);
        return new RecalculationJob(db, runner, RunHandler(db, clock), FormulaService(), clock);
    }

    private static RunCalculationHandler RunHandler(EcrDbContext db, TestClock clock)
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IRecalculationApprovalStore>(),
            Substitute.For<IAuditWriter>());

    /// <summary>Фаза формул шаблону нічого не пише — тест про прогони.</summary>
    private static RecalculationService FormulaService()
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var header = Substitute.For<IDocumentHeaderStore>();
        header.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new(
            Substitute.For<ICellStore>(),
            rows,
            Substitute.For<IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<IRegistryStore>(),
            header,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IUnitOfWork>(), Substitute.For<ISheetEditGate>());
    }

    /// <summary>Оркестратор із бар'єром: сигналить вхід і (за потреби) тримається до звільнення.</summary>
    private sealed class GatedRunner(bool holdUntilReleased) : ICalculationRunner
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<long> RunIds { get; } = [];

        public void Release() => release.TrySetResult();

        public async Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            lock (RunIds)
            {
                RunIds.Add(calculationRunId);
            }

            Entered.TrySetResult();

            if (holdUntilReleased)
            {
                await release.Task.WaitAsync(ct);
            }

            return new ModuleProfile();
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
