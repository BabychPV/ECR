// tests/Ecr.Infrastructure.Tests/Jobs/JobWorkerTests.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Integration;
using Ecr.Domain.Errors;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// <see cref="JobWorker"/> наскрізно на справжній базі: постановка через
/// <see cref="DbBackgroundJobScheduler"/> → захоплення → виконання → стан у
/// <c>itg.JobProgress</c> (MI-02, F1c).
/// </summary>
/// <remarks>
/// «Хост» — окремий <see cref="ServiceProvider"/> зі своїм воркером і своїми
/// з'єднаннями, як у різних процесів. Мутаційні докази — в описі коміту.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class JobWorkerTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Задача_з_черги_виконується_в_своєму_scope_з_орендою_і_стає_Succeeded()
    {
        var probe = new WorkerProbe();
        await using var host = await StartHostAsync(probe);

        var jobId = await EnqueueJobAsync<WorkerProbeJob>(host, new { documentId = 42L, n = 7 }, createdBy: null);

        var row = await WaitForStateAsync(jobId, "Succeeded");

        Assert.StartsWith(nameof(WorkerProbeJob) + "-", jobId, StringComparison.Ordinal);
        Assert.Equal(JobLanes.Default, row.Lane);
        Assert.Equal(42L, row.DocumentId);
        Assert.Equal(1, row.Attempt);
        Assert.Equal(JobProgressStore.CurrentInstanceId, row.InstanceId);

        var (payload, lease) = Assert.Single(probe.Runs);
        Assert.Equal(7, JsonDocument.Parse(payload!).RootElement.GetProperty("n").GetInt32());

        // ⛔ Оренда прив'язана до scope задачі — саме її читає fencing видимості.
        Assert.Equal(jobId, lease?.JobId);
    }

    [Fact]
    [Trait("Requirement", "ФВ-12.4a")]
    public async Task Транзієнтний_провал_повертає_в_чергу_з_затримкою_і_після_чотирьох_спроб_Failed()
    {
        var probe = new WorkerProbe();
        await using var host = await StartHostAsync(probe, o => o with { RetryDelay = n => TimeSpan.FromMilliseconds(400 * n) });

        var jobId = await EnqueueJobAsync<WorkerFailingJob>(host, new { n = 1 });

        var row = await WaitForStateAsync(jobId, "Failed");

        Assert.Equal(JobRetryPolicy.MaxRetryAttempts + 1, probe.Attempts.Count);
        Assert.Equal(JobRetryPolicy.MaxRetryAttempts + 1, row.Attempt);
        Assert.Equal(ErrorCodes.Internal, row.ErrorCode);

        // Затримки 400/800/1200 мс — наступна спроба не раніше за відступ (годинник СУБД ≈ хоста).
        var gaps = probe.Attempts.Zip(probe.Attempts.Skip(1), (a, b) => b - a).ToList();
        for (var i = 0; i < gaps.Count; i++)
        {
            Assert.True(
                gaps[i] >= TimeSpan.FromMilliseconds(400 * (i + 1) - 100),
                $"Спроба {i + 2} прийшла через {gaps[i].TotalMilliseconds:0} мс, раніше за відступ.");
        }
    }

    [Fact]
    public async Task Вердикт_а_не_збій_дороги_Failed_одразу_без_ретраїв()
    {
        var probe = new WorkerProbe();
        await using var host = await StartHostAsync(probe);

        var jobId = await EnqueueJobAsync<WorkerVerdictJob>(host, new { n = 1 });

        var row = await WaitForStateAsync(jobId, "Failed");

        Assert.Single(probe.Attempts);
        Assert.Equal("ECR-PRD-0404", row.ErrorCode);
    }

    [Fact]
    [Trait("Requirement", "L7-01")]
    public async Task Надто_глибокий_вираз_Failed_одразу_з_ECR_EXPR_0422_без_ретраїв()
    {
        // ⛔ L7-01: сторож стека обходу дерева виразу кидає InsufficientExecutionStackException.
        // Дерево від повтору не мілішає — раніше задача тричі повторювала той самий збій
        // і падала з ECR-SYS-0500, тобто без зрозумілої причини.
        var probe = new WorkerProbe();
        await using var host = await StartHostAsync(probe);

        var jobId = await EnqueueJobAsync<WorkerTooComplexJob>(host, new { n = 1 });

        var row = await WaitForStateAsync(jobId, "Failed");

        Assert.Single(probe.Attempts);
        Assert.Equal(ErrorCodes.ExpressionTooComplex, row.ErrorCode);
    }

    [Fact]
    public async Task Скасування_з_іншого_контексту_зупиняє_задачу_і_закриває_Cancelled()
    {
        var probe = new WorkerProbe();
        await using var host = await StartHostAsync(probe);

        var jobId = await EnqueueJobAsync<WorkerBlockingJob>(host, new { n = 1 });
        await probe.Started.Task.WaitAsync(Patience);

        // Інший «процес»: власний провайдер, власне з'єднання, лише порт планувальника.
        await using (var other = BuildHost(new WorkerProbe()))
        await using (var scope = other.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DbBackgroundJobScheduler>()
                .CancelAsync(jobId, CancellationToken.None);
        }

        await WaitForStateAsync(jobId, "Cancelled");
        Assert.True(await probe.Cancelled.Task.WaitAsync(Patience));
    }

    [Fact]
    public async Task Скасована_задача_що_кидає_не_OCE_закривається_Cancelled_а_не_ретраєм()
    {
        // L2-01 (в): драйвер посеред запиту кидає «Operation cancelled by user», а не OCE.
        var probe = new WorkerProbe();
        await using var host = await StartHostAsync(probe);

        var jobId = await EnqueueJobAsync<WorkerDriverCancelJob>(host, new { n = 1 });
        await probe.Started.Task.WaitAsync(Patience);

        await using (var other = BuildHost(new WorkerProbe()))
        await using (var scope = other.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DbBackgroundJobScheduler>()
                .CancelAsync(jobId, CancellationToken.None);
        }

        var row = await WaitForStateAsync(jobId, "Cancelled");
        Assert.Equal(1, row.Attempt);
        Assert.Single(probe.Attempts);

        // ⛔ Не провал: ні «буде повтор», ні коду помилки — це скасування людиною.
        Assert.DoesNotContain("retryScheduled", row.Message ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(row.ErrorCode);
    }

    [Fact]
    public async Task Зупинка_хоста_повертає_задачу_в_чергу_не_зараховуючи_спробу()
    {
        // L2-08 (D-208): зупинка — подія життєвого циклу, а не провал.
        var probe = new WorkerProbe();
        string jobId;
        await using (var host = await StartHostAsync(probe))
        {
            jobId = await EnqueueJobAsync<WorkerBlockingJob>(host, new { n = 1 });
            await probe.Started.Task.WaitAsync(Patience);
        }

        var row = await RowAsync(jobId);
        Assert.Equal("Queued", row!.State);
        Assert.Equal(0, row.Attempt ?? 0);
    }

    [Fact]
    public async Task Втрачена_оренда_зупиняє_задачу_і_нічого_не_пише_в_чужий_рядок()
    {
        var probe = new WorkerProbe();
        await using var host = await StartHostAsync(probe);

        var jobId = await EnqueueJobAsync<WorkerBlockingJob>(host, new { n = 1 });
        await probe.Started.Task.WaitAsync(Patience);

        // Інший виконавець перехопив оренду (новий токен) — подовження каже Lost.
        await ExecAsync("UPDATE itg.JobProgress SET ClaimToken = NEWID() WHERE JobId = @id;", jobId);

        Assert.True(await probe.Cancelled.Task.WaitAsync(Patience));
        await Task.Delay(300);

        var row = await RowAsync(jobId);
        Assert.Equal("Running", row?.State);
    }

    [Fact]
    [Trait("Requirement", "ФВ-12.3")]
    public async Task Два_хости_по_50_задач_кожна_виконана_рівно_раз_таблиця_свідок()
    {
        var table = $"dbo.JobWitness_{Guid.NewGuid():N}";
        await ExecSqlAsync($"CREATE TABLE {table} (N int NOT NULL, At datetime2(3) NOT NULL DEFAULT SYSUTCDATETIME());");

        try
        {
            var probe = new WorkerProbe();
            await using var enqueuer = BuildHost(probe);
            var ids = new List<string>();
            await using (var scope = enqueuer.CreateAsyncScope())
            {
                var jobs = scope.ServiceProvider.GetRequiredService<DbBackgroundJobScheduler>();
                for (var n = 0; n < 50; n++)
                {
                    ids.Add(await jobs.EnqueueAsync<WorkerWitnessJob>(new { n, table }, CancellationToken.None));
                }
            }

            await using (var a = await StartHostAsync(probe))
            await using (var b = await StartHostAsync(probe))
            {
                foreach (var id in ids)
                {
                    await WaitForStateAsync(id, "Succeeded");
                }
            }

            Assert.Equal(50, await ScalarAsync($"SELECT COUNT(*) FROM {table};"));
            Assert.Equal(50, await ScalarAsync($"SELECT COUNT(DISTINCT N) FROM {table};"));
        }
        finally
        {
            await ExecSqlAsync($"DROP TABLE {table};");
        }
    }

    private static async Task<string> EnqueueJobAsync<TJob>(ServiceProvider host, object payload, int? createdBy = null)
        where TJob : IBackgroundJob
    {
        await using var scope = host.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DbBackgroundJobScheduler>()
            .EnqueueAsync<TJob>(payload, CancellationToken.None, createdBy);
    }

    private async Task<JobProgress> WaitForStateAsync(string jobId, string state)
    {
        var clock = Stopwatch.StartNew();

        while (true)
        {
            var row = await RowAsync(jobId);
            if (row?.State == state)
            {
                return row;
            }

            Assert.True(clock.Elapsed < Patience, $"{jobId}: очікувався {state}, стан {row?.State ?? "—"}.");
            await Task.Delay(50);
        }
    }

    private ServiceProvider BuildHost(WorkerProbe probe)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Sql.CreateContext());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IJobQueue>(sp => new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddScoped<JobLeaseContext>();
        services.AddScoped<IJobLeaseContext>(sp => sp.GetRequiredService<JobLeaseContext>());
        services.AddSingleton<JobQueueSignal>();
        services.AddScoped(sp => new QuartzJobScheduler(null, sp.GetService<IJobProgressStore>(), sp.GetService<IClock>()));
        services.AddScoped<DbBackgroundJobScheduler>();
        services.AddSingleton(probe);
        services.AddScoped<WorkerProbeJob>();
        services.AddScoped<WorkerFailingJob>();
        services.AddScoped<WorkerVerdictJob>();
        services.AddScoped<WorkerTooComplexJob>();
        services.AddScoped<WorkerBlockingJob>();
        services.AddScoped<WorkerWitnessJob>();
        services.AddScoped<WorkerDriverCancelJob>();
        return services.BuildServiceProvider();
    }

    private async Task<WorkerHost> StartHostAsync(WorkerProbe probe, Func<JobWorkerOptions, JobWorkerOptions>? tune = null)
    {
        var provider = BuildHost(probe);
        var options = new JobWorkerOptions
        {
            Lanes = JobLanes.All,
            Role = JobProgressStore.CurrentRole,
            PollInterval = TimeSpan.FromMilliseconds(50),
            RenewInterval = TimeSpan.FromMilliseconds(100),
            RetryDelay = n => TimeSpan.FromMilliseconds(50 * n),
        };

        var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            tune?.Invoke(options) ?? options,
            provider.GetRequiredService<JobQueueSignal>(),
            NullLogger<JobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        return new WorkerHost(provider, worker);
    }

    private async Task ExecSqlAsync(string text)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Хост: провайдер і його воркер; зупинка — як у хоста застосунку.</summary>
    private sealed class WorkerHost(ServiceProvider provider, JobWorker worker) : IAsyncDisposable
    {
        public static implicit operator ServiceProvider(WorkerHost host) => host.Provider;

        public ServiceProvider Provider { get; } = provider;

        public async ValueTask DisposeAsync()
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await Provider.DisposeAsync();
        }
    }
}

/// <summary>Що бачили тестові задачі воркера.</summary>
public sealed class WorkerProbe
{
    public ConcurrentQueue<(string? Payload, JobClaimToken? Lease)> Runs { get; } = new();

    public ConcurrentQueue<TimeSpan> AttemptQueue { get; } = new();

    public List<TimeSpan> Attempts => [.. AttemptQueue];

    public Stopwatch Clock { get; } = Stopwatch.StartNew();

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class WorkerProbeJob(WorkerProbe probe, IJobLeaseContext lease) : IBackgroundJob
{
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        probe.Runs.Enqueue((payload as string, lease.Current));
        return Task.CompletedTask;
    }
}

public sealed class WorkerFailingJob(WorkerProbe probe) : IBackgroundJob
{
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        probe.AttemptQueue.Enqueue(probe.Clock.Elapsed);
        throw new TimeoutException("Транзієнтна відмова джерела.");
    }
}

public sealed class WorkerVerdictJob(WorkerProbe probe) : IBackgroundJob
{
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        probe.AttemptQueue.Enqueue(probe.Clock.Elapsed);
        throw new NotFoundException("ECR-PRD-0404", "Періоду немає.");
    }
}

public sealed class WorkerTooComplexJob(WorkerProbe probe) : IBackgroundJob
{
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        probe.AttemptQueue.Enqueue(probe.Clock.Elapsed);
        throw new InsufficientExecutionStackException("The expression is too complex to traverse: split it into several formulas.");
    }
}

public sealed class WorkerBlockingJob(WorkerProbe probe) : IBackgroundJob
{
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        probe.Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            probe.Cancelled.TrySetResult(true);
            throw;
        }
    }
}

/// <summary>Як драйвер БД: на скасування токена кидає НЕ <see cref="OperationCanceledException"/>.</summary>
public sealed class WorkerDriverCancelJob(WorkerProbe probe) : IBackgroundJob
{
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        probe.AttemptQueue.Enqueue(probe.Clock.Elapsed);
        probe.Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException("Operation cancelled by user.");
        }
    }
}

public sealed class WorkerWitnessJob(EcrDbContext db) : IBackgroundJob
{
    public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse((string)payload!);
        var n = doc.RootElement.GetProperty("n").GetInt32();
        var table = doc.RootElement.GetProperty("table").GetString()!;

#pragma warning disable EF1002 // Ім'я таблиці-свідка — з самого тесту, не від користувача.
        await db.Database.ExecuteSqlRawAsync($"INSERT INTO {table} (N) VALUES ({{0}});", [n], ct);
#pragma warning restore EF1002
    }
}
