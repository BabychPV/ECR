// tests/Ecr.Infrastructure.Tests/Jobs/SupersededResultsSweepJobTests.cs
using Ecr.Application.Ports;
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
/// Q-17: нічна задача <see cref="SupersededResultsSweepJob"/> - разове прибирання й страховка швидкого шляху
/// (рішення людини 28.10: лишається лише актуальний розрахунок).
/// </summary>
/// <remarks>
/// Мутаційні докази: замінити <c>NOT IN (Current, Running)</c> у <c>NonCurrentWhere</c> на <c>= 'Superseded'</c> -
/// тест Failed/нульової партиції червоніє; прибрати перевірку невідомих статусів - тест aborted червоніє; прибрати
/// <c>DeleteEnabled</c> - dry-run видаляє; прибрати зупинку на журналі - тест logFull червоніє.
/// </remarks>
public sealed class SupersededResultsSweepJobTests(SweepDatabase database) : IClassFixture<SweepDatabase>
{
    private static readonly DateTime T0 = new(2026, 10, 28, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Dry_run_лічить_кандидатів_за_таблицями_і_нічого_не_видаляє()
    {
        var data = await ArrangeAsync(202601);

        var outcome = await RunAsync(PurgeKit.On() with { DeleteEnabled = false });

        Assert.Equal(SupersededSweepOutcome.DryRunMode, outcome.Mode);
        Assert.Equal(4, outcome.Candidates[SupersededResultsPurger.ResultTable]);
        Assert.Equal(3, outcome.Candidates[SupersededResultsPurger.StepTable]);
        Assert.Equal(3, outcome.Candidates[SupersededResultsPurger.InputTable]);
        Assert.True(outcome.StatusHistogram["Superseded"] >= 1);

        await using var db = data.Stand.Builder.CreateContext();
        Assert.Equal((3, 2, 2), await PurgeKit.CountsAsync(db, data.Superseded));
        Assert.Equal((1, 1, 1), await PurgeKit.CountsAsync(db, data.Failed));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Видалення_відновлюється_між_проходами_лишає_Current_і_Running_і_ідемпотентне()
    {
        var data = await ArrangeAsync(202602);
        var options = PurgeKit.On(batch: 2) with { MaxBatchesPerRun = 2 };

        var first = await RunAsync(options);
        Assert.Equal(SupersededSweepOutcome.DeleteMode, first.Mode);
        Assert.False(first.Complete, "стеля пакетів мала зупинити прохід раніше");
        Assert.Equal(2, first.Batches);

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
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(db, data.Failed));
        Assert.Equal((2, 1, 1), await PurgeKit.CountsAsync(db, data.Current));
        Assert.Equal((2, 1, 1), await PurgeKit.CountsAsync(db, data.Running));

        // Прогони лишилися всі чотири.
        Assert.Equal(4, await db.CalculationRuns.AsNoTracking().CountAsync(r => r.ProjectId == data.Stand.Document.ProjectId));

        var again = await RunAsync(options);
        Assert.True(again.Complete);
        Assert.Empty(again.Deleted);
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

        // Налаштування за замовчуванням (як у файлі): прибирання вимкнено.
        Assert.Contains("\"mode\":\"disabled\"", await RunJobAsync(new()), StringComparison.Ordinal);

        // Увімкнено без DeleteEnabled: лише dry-run.
        Assert.Contains(
            "\"mode\":\"dryRun\"",
            await RunJobAsync(new() { [SupersededPurgeOptions.EnabledKey] = "true" }),
            StringComparison.Ordinal);
        await using (var db = data.Stand.Builder.CreateContext())
        {
            Assert.Equal((3, 2, 2), await PurgeKit.CountsAsync(db, data.Superseded));
        }

        // DeleteEnabled=true: видаляє.
        Assert.Contains(
            "\"mode\":\"delete\"",
            await RunJobAsync(new()
            {
                [SupersededPurgeOptions.EnabledKey] = "true",
                [SupersededPurgeOptions.DeleteEnabledKey] = "true",
            }),
            StringComparison.Ordinal);
        await using var after = data.Stand.Builder.CreateContext();
        Assert.Equal((0, 0, 0), await PurgeKit.CountsAsync(after, data.Superseded));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перевірка_узгодженості_бачить_рядки_неактуальних_прогонів_лише_коли_прибирання_увімкнено()
    {
        var data = await ArrangeAsync(202608);

        async Task<bool> HasIssueAsync(bool enabled)
        {
            await using (var db = data.Stand.Builder.CreateContext())
            {
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [SupersededPurgeOptions.EnabledKey] = enabled ? "true" : "false",
                    })
                    .Build();
                var job = new ConsistencyCheckJob(
                    db, Substitute.For<IOrphanScanner>(), new TestClock(T0), Substitute.For<IConsistencyMetrics>(),
                    configuration);
                await job.ExecuteAsync(null, Substitute.For<IJobProgress>(), CancellationToken.None);
            }

            await using var verify = data.Stand.Builder.CreateContext();
            return await verify.Database
                .SqlQuery<int>($"""
                    SELECT COUNT(*) AS Value FROM aud.ConsistencyIssue
                     WHERE RuleCode = {ConsistencyCheckJob.NonCurrentRunRowsRule} AND EntityId = {data.Stand.Period}
                       AND ResolvedAt IS NULL
                    """)
                .SingleAsync() > 0;
        }

        Assert.False(await HasIssueAsync(enabled: false));
        Assert.True(await HasIssueAsync(enabled: true));
    }

    private async Task<SupersededSweepOutcome> RunAsync(
        SupersededPurgeOptions options, Func<CancellationToken, Task<double?>>? logUsed = null)
    {
        await using var db = new Ecr.Infrastructure.Persistence.EcrDbContext(
            new DbContextOptionsBuilder<Ecr.Infrastructure.Persistence.EcrDbContext>()
                .UseSqlServer(database.ConnectionString).Options);
        var job = new SupersededResultsSweepJob(
            db, new TestClock(T0), new ConfigurationBuilder().Build(), NullLogger<SupersededResultsSweepJob>.Instance,
            logUsed);
        return await job.RunAsync(options, progress: null, CancellationToken.None);
    }

    /// <summary>Документ і чотири прогони: Current (2/1/1), Superseded (3/2/2), Failed (1/1/1), Running (2/1/1).</summary>
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
        var current = await AddRunWithRowsAsync(stand, "Current", T0, 2, 1, 1);
        var superseded = await AddRunWithRowsAsync(stand, "Superseded", T0.AddHours(1), 3, 2, 2, documentScoped: false);
        var failed = await AddRunWithRowsAsync(stand, "Failed", T0.AddHours(2), 1, 1, 1, documentScoped: false);
        var running = await AddRunWithRowsAsync(stand, "Running", T0.AddHours(3), 2, 1, 1, documentScoped: false);
        return new SweepData(stand, current, superseded, failed, running);
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

    private sealed record SweepData(PurgeStand Stand, long Current, long Superseded, long Failed, long Running);
}
