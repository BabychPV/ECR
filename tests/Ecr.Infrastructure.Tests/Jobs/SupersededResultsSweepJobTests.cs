// tests/Ecr.Infrastructure.Tests/Jobs/SupersededResultsSweepJobTests.cs
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Tests.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>Окрема тимчасова база для нічної задачі: прохід глобальний і не повинен чіпати спільну базу колекції.</summary>
public sealed class SweepDatabase : IAsyncLifetime
{
    private readonly SqlServerFixture database = SqlServerFixture.WithOwnDatabase("_supsweep");

    /// <summary>Рядок підключення до окремої бази.</summary>
    public string ConnectionString => database.ConnectionString;

    /// <inheritdoc />
    public async Task InitializeAsync() => await database.InitializeAsync();

    /// <inheritdoc />
    public async Task DisposeAsync() => await database.DisposeAsync();
}

/// <summary>
/// Q-17 / <c>D-560</c>: нічна задача <see cref="SupersededResultsSweepJob"/> - разове прибирання й страховка швидкого
/// шляху (рішення людини 20.11: для «документ × період» лишається лише останній ланцюг; прибраний прогін зникає
/// повністю, разом із рядком <c>calc.CalculationRun</c>; актуальний результат є завжди).
/// </summary>
/// <remarks>
/// Мутаційні докази: додати <c>Current</c> до <c>SupersededResultsPurger.EligibleSql</c> - тести «Current/Running
/// цілі» і гонитви з перерахунком червоніють; прибрати умову «новіший Current у ланцюгу» для <c>Failed</c> - тест
/// «Failed новіший за актуальний лишається» червоніє; прибрати перевірку невідомих статусів - тест aborted червоніє;
/// прибрати <c>DeleteEnabled</c> - dry-run видаляє; прибрати зупинку на журналі - тест logFull червоніє.
/// </remarks>
public sealed class SupersededResultsSweepJobTests(SweepDatabase database) : IClassFixture<SweepDatabase>
{
    private static readonly DateTime T0 = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Годинник проходу: понад <see cref="SupersededPurgeOptions.FailedRunGrace"/> після прогонів стенду.</summary>
    private static readonly DateTime SweepNow = T0.AddDays(3);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Dry_run_лічить_кандидатів_за_таблицями_і_прогонами_і_нічого_не_видаляє()
    {
        var data = await ArrangeAsync(202601);

        var outcome = await RunAsync(PurgeKit.On() with { DeleteEnabled = false });

        // Кандидати: Superseded (3/2/2) і Failed, старіший за актуальний (1/1/1); Failed новіший - ні.
        Assert.Equal(SupersededSweepOutcome.DryRunMode, outcome.Mode);
        Assert.Equal(4, outcome.Candidates[SupersededResultsPurger.ResultTable]);
        Assert.Equal(3, outcome.Candidates[SupersededResultsPurger.StepTable]);
        Assert.Equal(3, outcome.Candidates[SupersededResultsPurger.InputTable]);
        Assert.Equal(2, outcome.Candidates[SupersededResultsPurger.RunTable]);
        Assert.Equal(2, outcome.RunCandidates);
        Assert.True(outcome.RunIndexes);
        Assert.True(outcome.StatusHistogram["Superseded"] >= 1);

        await using var db = data.Stand.Builder.CreateContext();
        Assert.Equal((3, 2, 2), await PurgeKit.CountsAsync(db, data.Superseded));
        Assert.Equal((1, 1, 1), await PurgeKit.CountsAsync(db, data.FailedOld));
        Assert.True(await PurgeKit.RunExistsAsync(db, data.Superseded));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_відновлюється_між_проходами_лишає_Current_Running_і_останню_спробу_і_ідемпотентне()
    {
        var data = await ArrangeAsync(202602);
        var options = PurgeKit.On(batch: 2) with { MaxBatchesPerRun = 2 };

        var first = await RunAsync(options);
        Assert.Equal(SupersededSweepOutcome.DeleteMode, first.Mode);
        Assert.False(first.Complete, "стеля пакетів мала зупинити прохід раніше");
        Assert.Equal(2, first.Batches);
        Assert.Equal("maxBatches", first.StopReason);

        // Наступні проходи доїдають решту; прогрес - у самих даних.
        var rounds = 0;
        SupersededSweepOutcome last;
        do
        {
            last = await RunAsync(options);
            rounds++;
        }
        while (!last.Complete && rounds < 30);

        Assert.True(last.Complete);

        await using var db = data.Stand.Builder.CreateContext();
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(db, data.Superseded));
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(db, data.FailedOld));
        Assert.False(await PurgeKit.RunExistsAsync(db, data.Superseded));
        Assert.False(await PurgeKit.RunExistsAsync(db, data.FailedOld));

        // Ланцюг документа: актуальний, остання (невдала) спроба після нього і той, що ще рахується, - цілі.
        Assert.Equal((2, 1, 1), await PurgeKit.CountsAsync(db, data.Current));
        Assert.Equal((1, 1, 1), await PurgeKit.CountsAsync(db, data.FailedNew));
        Assert.Equal((2, 1, 1), await PurgeKit.CountsAsync(db, data.Running));
        Assert.Equal(
            [data.Current, data.FailedNew, data.Running],
            await db.CalculationRuns.AsNoTracking()
                .Where(r => r.ProjectId == data.Stand.Document.ProjectId)
                .OrderBy(r => r.Id)
                .Select(r => r.Id)
                .ToListAsync());

        var again = await RunAsync(options);
        Assert.True(again.Complete);
        Assert.Empty(again.Deleted);
        Assert.Equal(0, again.RunCandidates);
    }

    /// <remarks>
    /// ⛔ <c>Failed</c> може бути «живим»: <c>AbandonedWorkSweeper</c> закриває <c>Running</c> старший за 2 год, а прогін,
    /// завершившись, ще зробить себе актуальним. Тому <c>Failed</c> прибирається лише через
    /// <see cref="SupersededPurgeOptions.FailedRunGrace"/> від старту. Мутація: прибрати <c>StartedAt &lt; @failedBefore</c>
    /// з <c>EligibleSql</c> - тест червоніє.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Failed_молодший_за_добу_не_прибирається_навіть_старіший_за_актуальний()
    {
        var data = await ArrangeAsync(202610);

        var young = await RunAsync(PurgeKit.On(), now: T0.AddHours(6));
        Assert.True(young.Complete);

        await using var db = data.Stand.Builder.CreateContext();
        Assert.Equal((1, 1, 1), await PurgeKit.CountsAsync(db, data.FailedOld));
        Assert.True(await PurgeKit.RunExistsAsync(db, data.FailedOld));

        // Superseded - кінцевий стан, не чекає.
        Assert.False(await PurgeKit.RunExistsAsync(db, data.Superseded));

        await RunAsync(PurgeKit.On());
        Assert.False(await PurgeKit.RunExistsAsync(db, data.FailedOld));
    }

    /// <summary>
    /// ✎ D-560, конкурентність: перерахунки того самого документа й періоду (новий <c>Running</c> пише рядки → перемикання
    /// актуальності → швидкий шлях прибирання) ідуть паралельно з нічним проходом, а читач актуального результату весь
    /// цей час читає. Читач ЖОДНОГО разу не отримує порожньо чи неповно; наприкінці лишається рівно один актуальний
    /// прогін із повним набором рядків, і жодного прогону, що підлягає прибиранню.
    /// </summary>
    /// <remarks>
    /// Мутація: додати <c>Current</c> до <c>EligibleSql</c> - читач бачить порожньо, актуальний без рядків.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прибирання_паралельно_з_перерахунками_ніколи_не_лишає_період_без_актуального_результату()
    {
        var data = await ArrangeAsync(202611);
        var stand = data.Stand;
        const int Recalculations = 20;
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // Історія на старт: десять замінених прогонів документа з рядками - нічному проходу є що їсти.
        for (var i = 0; i < 10; i++)
        {
            await AddRunWithRowsAsync(stand, "Superseded", T0.AddMinutes(-100 + i), 40, 40, 40);
        }

        var recalculating = true;
        var problems = new System.Collections.Concurrent.ConcurrentQueue<string>();

        async Task RecalculateAsync()
        {
            try
            {
                for (var i = 1; i <= Recalculations; i++)
                {
                    long next;
                    await using (var db = stand.Builder.CreateContext())
                    {
                        next = await PurgeKit.AddRunAsync(db, stand, "Running", T0.AddMinutes(i));
                        await PurgeKit.InsertRowsAsync(db, stand, next, 2, 1, 1, "N" + i);
                    }

                    await CompleteAsync(stand, next, cts.Token);
                }
            }
            finally
            {
                Volatile.Write(ref recalculating, false);
            }
        }

        async Task SweepAsync()
        {
            while (Volatile.Read(ref recalculating))
            {
                await RunAsync(PurgeKit.On(batch: 7));
            }

            await RunAsync(PurgeKit.On(batch: 7));
        }

        async Task ReadAsync()
        {
            var reads = 0;
            while (Volatile.Read(ref recalculating) || reads == 0)
            {
                await using var db = stand.Builder.CreateContext();
                var rows = await new CalculationResultStore(db, new TestClock(SweepNow))
                    .ReadCurrentAsync(stand.Document.DocumentId, stand.Period, cts.Token);
                if (rows.Count != 2)
                {
                    problems.Enqueue($"читання {reads}: {rows.Count} рядків замість 2");
                }

                reads++;
            }
        }

        await Task.WhenAll(RecalculateAsync(), SweepAsync(), ReadAsync());

        Assert.Empty(problems);

        await using var check = stand.Builder.CreateContext();
        var current = await check.CalculationRuns.AsNoTracking()
            .Where(r => r.DocumentId == stand.Document.DocumentId && r.Status == "Current")
            .Select(r => r.Id)
            .ToListAsync();
        var only = Assert.Single(current);
        Assert.Equal((2, 1, 1), await PurgeKit.CountsAsync(check, only));
        var rows = await new CalculationResultStore(check, new TestClock(SweepNow))
            .ReadCurrentAsync(stand.Document.DocumentId, stand.Period, CancellationToken.None);
        Assert.All(rows, r => Assert.Equal("N" + Recalculations, r.OutputCode));

        // Замінених прогонів документа не лишилось - ні рядків, ні самих прогонів.
        Assert.Equal(
            0,
            await check.CalculationRuns.AsNoTracking()
                .CountAsync(r => r.DocumentId == stand.Document.DocumentId && r.Status == "Superseded"));
        Assert.Equal(0, await PurgeKit.NonCurrentRowsAsync(check, stand) - await FailedNewRowsAsync(check, data));
    }

    /// <summary>Рядки останньої (невдалої) спроби стенду - вона законно лишається поза «Current/Running».</summary>
    private static async Task<int> FailedNewRowsAsync(EcrDbContext db, SweepData data)
    {
        var (results, steps, inputs) = await PurgeKit.CountsAsync(db, data.FailedNew);
        return results + steps + inputs;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_індексів_прогону_запасна_форма_прибирає_лише_рядки()
    {
        var data = await ArrangeAsync(202612);
        await ExecuteAsync("""
            DROP INDEX IX_CalculationStep_Run ON calc.CalculationStep;
            """);
        try
        {
            var outcome = await RunAsync(PurgeKit.On(batch: 2));

            Assert.False(outcome.RunIndexes);
            Assert.True(outcome.Complete);

            await using var db = data.Stand.Builder.CreateContext();
            Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(db, data.Superseded));
            Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(db, data.FailedOld));
            Assert.Equal((2, 1, 1), await PurgeKit.CountsAsync(db, data.Current));
            Assert.Equal((1, 1, 1), await PurgeKit.CountsAsync(db, data.FailedNew));

            // Рядки прогонів лишаються: без індексу перевірка FK сканувала б усю таблицю.
            Assert.True(await PurgeKit.RunExistsAsync(db, data.Superseded));
        }
        finally
        {
            await ExecuteAsync("""
                CREATE NONCLUSTERED INDEX IX_CalculationStep_Run ON calc.CalculationStep (CalculationRunId)
                    WITH (DATA_COMPRESSION = PAGE) ON ps_ByPeriodKey (PeriodKey);
                """);
        }

        // З індексом наступний прохід доїдає й рядки прогонів.
        await RunAsync(PurgeKit.On());
        await using var after = data.Stand.Builder.CreateContext();
        Assert.False(await PurgeKit.RunExistsAsync(after, data.Superseded));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Невідомий_статус_зупиняє_прохід_у_обох_режимах_і_нічого_не_видаляє()
    {
        var data = await ArrangeAsync(202603);
        var weird = await AddRunWithRowsAsync(data.Stand, "Weird", T0.AddHours(5), 1, 1, 1);

        foreach (var deleteEnabled in new[] { false, true })
        {
            var outcome = await RunAsync(PurgeKit.On() with { DeleteEnabled = deleteEnabled });
            Assert.Equal(SupersededSweepOutcome.AbortedMode, outcome.Mode);
            Assert.Contains("Weird", outcome.AbortReason, StringComparison.Ordinal);
            Assert.Equal(1, outcome.StatusHistogram["Weird"]);
        }

        await using var db = data.Stand.Builder.CreateContext();
        Assert.Equal((3, 2, 2), await PurgeKit.CountsAsync(db, data.Superseded));
        Assert.Equal((1, 1, 1), await PurgeKit.CountsAsync(db, weird));

        // Прибрати за собою: інакше «невідомий» статус зупиняв би і сусідні тести цієї бази.
        await db.CalculationRuns.Where(r => r.Id == weird).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Failed"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Прогін_без_періоду_у_статусі_Failed_прибирається_нульова_партиція()
    {
        var data = await ArrangeAsync(202604);
        await using var db = data.Stand.Builder.CreateContext();
        var legacy = await PurgeKit.AddRunAsync(db, data.Stand, "Superseded", T0, documentScoped: false, noPeriod: true);
        await PurgeKit.InsertRowsAsync(db, data.Stand, legacy, 3, 1, 1, "Z", period: 0);

        var outcome = await RunAsync(PurgeKit.On(batch: 2));

        Assert.True(outcome.Complete);
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(db, legacy));
        Assert.False(await PurgeKit.RunExistsAsync(db, legacy));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task При_FULL_recovery_і_заповненому_журналі_прохід_зупиняється_а_потім_продовжує()
    {
        var data = await ArrangeAsync(202605);
        await SetRecoveryAsync("FULL");
        try
        {
            var options = PurgeKit.On(batch: 2) with { LogPauseSeconds = 0, MaxLogPauses = 1 };

            var stopped = await RunAsync(options, logUsed: _ => Task.FromResult<double?>(95));
            Assert.False(stopped.Complete);
            Assert.Equal("logFull", stopped.StopReason);
            Assert.Equal("FULL", stopped.RecoveryModel);
            Assert.Equal(1, stopped.Batches);

            var resumed = await RunAsync(options, logUsed: _ => Task.FromResult<double?>(10));
            Assert.True(resumed.Complete);

            await using var db = data.Stand.Builder.CreateContext();
            Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(db, data.Superseded));
        }
        finally
        {
            await SetRecoveryAsync("SIMPLE");
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перебудова_після_повного_прибирання_виконується_за_прапорцем()
    {
        await ArrangeAsync(202606);

        var outcome = await RunAsync(PurgeKit.On() with { RebuildAfter = true });

        Assert.True(outcome.Complete);
        Assert.True(outcome.Rebuilt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task ExecuteAsync_пише_журнал_обслуговування_за_режимами()
    {
        var data = await ArrangeAsync(202607);

        async Task<string> RunJobAsync(Dictionary<string, string?> settings)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            await using (var db = data.Stand.Builder.CreateContext())
            {
                var job = new SupersededResultsSweepJob(
                    db, new TestClock(T0), configuration, NullLogger<SupersededResultsSweepJob>.Instance);
                await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
            }

            await using var verify = data.Stand.Builder.CreateContext();
            var run = await verify.MaintenanceRuns.AsNoTracking()
                .Where(r => r.JobCode == SupersededResultsSweepJob.Code)
                .OrderByDescending(r => r.Id)
                .FirstAsync();
            Assert.Equal("Succeeded", run.Status);
            return run.DetailsJson!;
        }

        // Явно вимкнено: нічого не лічить і не видаляє.
        Assert.Contains(
            "\"mode\":\"disabled\"",
            await RunJobAsync(new() { [SupersededPurgeOptions.EnabledKey] = "false" }),
            StringComparison.Ordinal);

        // Увімкнено, DeleteEnabled=false: лише dry-run.
        Assert.Contains(
            "\"mode\":\"dryRun\"",
            await RunJobAsync(new()
            {
                [SupersededPurgeOptions.EnabledKey] = "true",
                [SupersededPurgeOptions.DeleteEnabledKey] = "false",
            }),
            StringComparison.Ordinal);
        await using (var db = data.Stand.Builder.CreateContext())
        {
            Assert.Equal((3, 2, 2), await PurgeKit.CountsAsync(db, data.Superseded));
        }

        // ✎ D-560: без жодного ключа (служба EcrWorker не читає appsettings.json Api) - увімкнено й видаляє.
        var details = await RunJobAsync(new());
        Assert.Contains("\"mode\":\"delete\"", details, StringComparison.Ordinal);
        Assert.Contains("\"calc.CalculationRun\":1", details, StringComparison.Ordinal);
        await using var after = data.Stand.Builder.CreateContext();
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(after, data.Superseded));
        Assert.False(await PurgeKit.RunExistsAsync(after, data.Superseded));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перевірка_узгодженості_бачить_рядки_неактуальних_прогонів_лише_коли_прибирання_увімкнено()
    {
        var data = await ArrangeAsync(202608);

        // ✎ Аудит live 12.11: правило лічить лише рядки прогонів, неактуальних уже на старті останнього успішного
        // прибирання (див. `ConsistencyCheckJob.NonCurrentRunRowsAsync`). Тож щоб правило мало що доводити, у журналі
        // має бути завершене прибирання ПІСЛЯ прогонів цього стенду (T0…T0+3h) — інакше тест перевіряв би не правило,
        // а годинник. Без цього рядка тест і на старому коді, і на новому залежав би від сусідніх тестів класу.
        await AddCompletedSweepAsync(data.Stand, T0.AddDays(1));

        Assert.False(await HasNonCurrentIssueAsync(data.Stand, purgeEnabled: false));
        Assert.True(await HasNonCurrentIssueAsync(data.Stand, purgeEnabled: true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перевірка_узгодженості_не_лічить_прогони_витіснені_після_останнього_прибирання()
    {
        // ⛔ Аудит live 12.11: 30.10 перевірка й прибирання стартували одним cron за 10 мс, і перевірка порахувала
        // рядки, які прибирання саме видаляло, — сім знахідок, неправдивих уже за секунду. Так само ручний запуск
        // посеред перерахунку бачив щойно витіснений прогін. Рядок, якого прибирання ще не мало шансу побачити, —
        // не порушення. RED до фіксу: знахідка з'являлась і тут.
        var data = await ArrangeAsync(202609);

        // Прибирання пройшло, а ПІСЛЯ нього новіший прогін того самого проєкту й періоду витіснив попередні.
        await AddCompletedSweepAsync(data.Stand, T0.AddDays(20));
        await AddRunWithRowsAsync(data.Stand, "Superseded", T0.AddDays(30), 1, 1, 1, documentScoped: false);

        Assert.False(await HasNonCurrentIssueAsync(data.Stand, purgeEnabled: true));

        // Зворотний бік: щойно прибирання пройшло вже ПІСЛЯ витіснення, ті самі рядки — справжня знахідка.
        await AddCompletedSweepAsync(data.Stand, T0.AddDays(40));
        Assert.True(await HasNonCurrentIssueAsync(data.Stand, purgeEnabled: true));
    }

    /// <remarks>
    /// ✎ D-560: остання (невдала) спроба, новіша за актуальний прогін, лишається свідомо - перевірка узгодженості не
    /// має називати її рядки порушенням. RED на правилі «будь-який не Current/Running».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перевірка_узгодженості_не_лічить_Failed_новіший_за_актуальний()
    {
        var data = await ArrangeAsync(202701);
        await RunAsync(PurgeKit.On());
        await AddCompletedSweepAsync(data.Stand, T0.AddDays(20));

        await using (var db = data.Stand.Builder.CreateContext())
        {
            Assert.Equal((1, 1, 1), await PurgeKit.CountsAsync(db, data.FailedNew));
        }

        Assert.False(await HasNonCurrentIssueAsync(data.Stand, purgeEnabled: true));
    }

    /// <summary>Запускає перевірку узгодженості й каже, чи є ВІДКРИТА знахідка NON_CURRENT_RUN_ROWS періоду стенду.</summary>
    /// <param name="stand">Стенд.</param>
    /// <param name="purgeEnabled">Значення <c>Calculations:SupersededPurge:Enabled</c>.</param>
    private static async Task<bool> HasNonCurrentIssueAsync(PurgeStand stand, bool purgeEnabled)
    {
        await using (var db = stand.Builder.CreateContext())
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SupersededPurgeOptions.EnabledKey] = purgeEnabled ? "true" : "false",
                })
                .Build();
            var job = new ConsistencyCheckJob(
                db, Substitute.For<IOrphanScanner>(), new TestClock(T0.AddDays(60)), Substitute.For<IConsistencyMetrics>(),
                configuration);
            await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
        }

        await using var verify = stand.Builder.CreateContext();
        return await verify.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM aud.ConsistencyIssue
                 WHERE RuleCode = {ConsistencyCheckJob.NonCurrentRunRowsRule} AND EntityId = {stand.Period}
                   AND ResolvedAt IS NULL
                """)
            .SingleAsync() > 0;
    }

    /// <summary>Записує в журнал обслуговування завершений успішний прохід прибирання, що стартував <paramref name="at"/>.</summary>
    private static async Task AddCompletedSweepAsync(PurgeStand stand, DateTime at)
    {
        await using var db = stand.Builder.CreateContext();
        var run = new MaintenanceRun(SupersededResultsSweepJob.Code, at);
        run.Complete("Succeeded", "{}", at.AddMinutes(1));
        db.MaintenanceRuns.Add(run);
        await db.SaveChangesAsync();
    }

    private async Task<SupersededSweepOutcome> RunAsync(
        SupersededPurgeOptions options, Func<CancellationToken, Task<double?>>? logUsed = null, DateTime? now = null)
    {
        await using var db = new Ecr.Infrastructure.Persistence.EcrDbContext(
            new DbContextOptionsBuilder<Ecr.Infrastructure.Persistence.EcrDbContext>()
                .UseSqlServer(database.ConnectionString).Options);
        var job = new SupersededResultsSweepJob(
            db, new TestClock(now ?? SweepNow), new ConfigurationBuilder().Build(),
            NullLogger<SupersededResultsSweepJob>.Instance, logUsed);
        return await job.RunAsync(options, progress: null, CancellationToken.None);
    }

    /// <summary>Завершення перерахунку як у задачі: перемикання актуальності + швидкий шлях прибирання після коміту.</summary>
    private static async Task CompleteAsync(PurgeStand stand, long runId, CancellationToken ct)
    {
        await using var db = stand.Builder.CreateContext();
        var clock = new TestClock(SweepNow);

        var handler = new Ecr.Application.Calculations.RunCalculationHandler(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock, purgeOptions: PurgeKit.On()),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<Ecr.Application.Common.ICurrentUser>(),
            clock,
            Substitute.For<IAuditWriter>());

        await handler.CompleteAsync(runId, new Ecr.Application.Calculations.ModuleProfile(), ct);
    }

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Документ і п'ять прогонів, за зростанням Id: Failed старіший за актуальний (1/1/1, документ), Current (2/1/1,
    /// документ), Superseded (3/2/2, проєкт), Failed новіший за актуальний (1/1/1, документ), Running (2/1/1, проєкт).
    /// </summary>
    private async Task<SweepData> ArrangeAsync(int periodKey)
    {
        // Детермінована модель відновлення (нова база може бути FULL з заповненим журналом - прохід чекав би).
        await SetRecoveryAsync("SIMPLE");

        // Чиста база для порівнянь: залишки інших тестів цього класу прибрано (прохід глобальний).
        SupersededSweepOutcome normalized;
        do
        {
            normalized = await RunAsync(PurgeKit.On(batch: 500));
        }
        while (!normalized.Complete);

        var stand = await PurgeKit.ArrangeAsync(database.ConnectionString, periodKey);
        var failedOld = await AddRunWithRowsAsync(stand, "Failed", T0.AddHours(-1), 1, 1, 1);
        var current = await AddRunWithRowsAsync(stand, "Current", T0, 2, 1, 1);
        var superseded = await AddRunWithRowsAsync(stand, "Superseded", T0.AddHours(1), 3, 2, 2, documentScoped: false);
        var failedNew = await AddRunWithRowsAsync(stand, "Failed", T0.AddHours(2), 1, 1, 1);
        var running = await AddRunWithRowsAsync(stand, "Running", T0.AddHours(3), 2, 1, 1, documentScoped: false);
        return new SweepData(stand, current, superseded, failedOld, failedNew, running);
    }

    private static async Task<long> AddRunWithRowsAsync(
        PurgeStand stand, string status, DateTime at, int results, int steps, int inputs, bool documentScoped = true)
    {
        await using var db = stand.Builder.CreateContext();
        var id = await PurgeKit.AddRunAsync(db, stand, status, at, documentScoped);
        await PurgeKit.InsertRowsAsync(db, stand, id, results, steps, inputs, status[..1]);
        return id;
    }

    private async Task SetRecoveryAsync(string model)
    {
        await using var db = new Ecr.Infrastructure.Persistence.EcrDbContext(
            new DbContextOptionsBuilder<Ecr.Infrastructure.Persistence.EcrDbContext>()
                .UseSqlServer(database.ConnectionString).Options);
        var name = await db.Database.SqlQuery<string>($"SELECT DB_NAME() AS Value").SingleAsync();
        var sql = model == "FULL"
            ? $"ALTER DATABASE [{name}] SET RECOVERY FULL"
            : $"ALTER DATABASE [{name}] SET RECOVERY SIMPLE";
        await using var master = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
        await master.OpenAsync();
        await using var command = master.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed record SweepData(
        PurgeStand Stand, long Current, long Superseded, long FailedOld, long FailedNew, long Running);
}
