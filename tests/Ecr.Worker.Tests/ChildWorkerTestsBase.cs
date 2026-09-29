// tests/Ecr.Worker.Tests/ChildWorkerTestsBase.cs

using System.Collections.Concurrent;
using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Integration;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Worker.Tests;

/// <summary>Спільне для тестів дочірнього воркера на справжній базі.</summary>
/// <remarks>
/// ⚠ Кожен тест стартує з порожньої черги (<c>Lane IS NOT NULL</c>) і лишає її
/// порожньою: живий дочірній процес узяв би чужу задачу recalc.
/// </remarks>
public abstract class ChildWorkerTestsBase(SqlServerFixture sql) : IAsyncLifetime
{
    protected static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    protected SqlServerFixture Sql => sql;

    public Task InitializeAsync() => PurgeQueueAsync();

    public Task DisposeAsync() => PurgeQueueAsync();

    /// <summary>Документ зі своїм проєктом і відкритим періодом (<see cref="TestDocumentBuilder"/>).</summary>
    protected Task<TestDocument> DocumentAsync() => new TestDocumentBuilder(sql.ConnectionString).BuildAsync();

    /// <summary>Payload перерахунку одного документа й періоду — той, що ставить <c>RecalculateDocumentHandler</c>.</summary>
    protected static RecalculationRequest RecalcOf(TestDocument document)
        => new(document.ProjectId, document.DocumentId, document.PeriodKey.Value, TriggeredByUserId: null);

    /// <summary>Постановка тим самим шляхом, що в Api за <c>Jobs:Queue:Mode = Database</c>.</summary>
    protected async Task<string> EnqueueAsync<TJob>(object payload)
        where TJob : IBackgroundJob
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => sql.CreateContext());
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IJobQueue>(sp => new DbJobQueue(sp.GetRequiredService<EcrDbContext>(), sp.GetRequiredService<IClock>()));
        services.AddScoped<IJobProgressStore>(sp => new JobProgressStore(sp.GetRequiredService<EcrDbContext>()));
        services.AddSingleton<JobQueueSignal>();
        services.AddScoped(sp => new QuartzJobScheduler(null, sp.GetService<IJobProgressStore>(), sp.GetService<IClock>()));
        services.AddScoped<DbBackgroundJobScheduler>();

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DbBackgroundJobScheduler>()
            .EnqueueAsync<TJob>(payload, CancellationToken.None);
    }

    protected async Task<JobProgress?> RowAsync(string jobId)
    {
        await using var db = sql.CreateContext();
        return await db.JobProgresses.AsNoTracking().SingleOrDefaultAsync(p => p.JobId == jobId);
    }

    protected async Task<JobProgress> WaitForStateAsync(string jobId, string state, Func<string>? diagnostics = null)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var row = await RowAsync(jobId);
            if (row?.State == state)
            {
                return row;
            }

            Assert.True(
                clock.Elapsed < Patience,
                $"{jobId}: очікувався {state}, стан {row?.State ?? "—"} ({row?.Error}). {diagnostics?.Invoke()}");
            await Task.Delay(100);
        }
    }

    protected async Task<List<CalculationRun>> RunsAsync(int projectId)
    {
        await using var db = sql.CreateContext();
        return await db.CalculationRuns.AsNoTracking().Where(r => r.ProjectId == projectId).OrderBy(r => r.Id).ToListAsync();
    }

    protected async Task ExecAsync(string text, string jobId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.AddWithValue("@id", jobId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Справжній <c>Ecr.Worker --child</c> з тестової збірки на тестову базу.</summary>
    protected ChildProcess StartChild()
    {
        var command = WorkerProcess.Command("--child");
        var info = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Як від служби-наглядача: рядок підключення — лише з оточення (D-11).
        info.Environment["ECR_ConnectionStrings__Ecr"] = sql.ConnectionString;
        info.Environment["DOTNET_ENVIRONMENT"] = "Production";

        var process = Process.Start(info) ?? throw new InvalidOperationException("Дочірній воркер не запущено.");
        var output = new ConcurrentQueue<string>();

        // ⚠ Потоки читаються завжди: повний канал stdout зупинив би дочірній на записі журналу.
        process.OutputDataReceived += (_, e) => Keep(output, e.Data);
        process.ErrorDataReceived += (_, e) => Keep(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new ChildProcess(process, output);
    }

    private static void Keep(ConcurrentQueue<string> output, string? line)
    {
        if (line is not null)
        {
            output.Enqueue(line);
            while (output.Count > 200)
            {
                output.TryDequeue(out _);
            }
        }
    }

    private async Task PurgeQueueAsync()
    {
        await using var db = sql.CreateContext();
        await db.JobProgresses.Where(p => p.Lane != null).ExecuteDeleteAsync();
    }

    /// <summary>Дочірній процес і хвіст його виводу; <c>Dispose</c> — прибирання без сиріт.</summary>
    protected sealed class ChildProcess(Process process, ConcurrentQueue<string> output) : IDisposable
    {
        public Process Process { get; } = process;

        public string Tail() => $"Вихід дочірнього: {string.Join(" | ", output.TakeLast(20))}";

        public void Dispose() => WorkerProcess.Kill(Process);
    }
}
