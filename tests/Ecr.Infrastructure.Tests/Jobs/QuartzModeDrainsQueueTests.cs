// tests/Ecr.Infrastructure.Tests/Jobs/QuartzModeDrainsQueueTests.cs
using System.Diagnostics;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// L2-04 (аудит 2026-10-03): після перемикання <c>Database → Quartz</c> (runbook §10.1,
/// <c>deploy-ecr.ps1 -DisableWorker</c>) рядки черги, поставлені до перемикання, не
/// лишаються «в черзі» назавжди — їх довиконує дренаж складання Api.
/// </summary>
/// <remarks>
/// ⚠ Складання — справжнє <c>AddEcrInfrastructure</c> з <c>Jobs:Queue:Mode = Quartz</c>;
/// запускається лише його виконавець черги (Quartz тут не потрібен).
/// Мутаційний доказ — в описі коміту.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
public sealed class QuartzModeDrainsQueueTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    [Fact]
    [Trait("Finding", "L2-04")]
    public async Task Рядок_черги_поставлений_до_перемикання_на_Quartz_довиконується()
    {
        string jobId;
        await using (var host = NewHost())
        {
            jobId = (await host.Queue.EnqueueAsync(
                    new JobEnqueueRequest(typeof(DrainProbeJob).FullName!, JobLanes.Default, "{}", null),
                    CancellationToken.None))
                .JobId;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ecr"] = Sql.ConnectionString,
                [DbBackgroundJobScheduler.ModeKey] = nameof(JobQueueMode.Quartz),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddEcrInfrastructure(configuration);
        services.AddScoped<DrainProbeJob>();
        await using var provider = services.BuildServiceProvider();

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobWorker));
        using var worker = new JobWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<JobWorkerOptions>(),
            provider.GetRequiredService<JobQueueSignal>(),
            NullLogger<JobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var clock = Stopwatch.StartNew();
            while ((await RowAsync(jobId))?.State != "Succeeded")
            {
                Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"{jobId}: стан {(await RowAsync(jobId))?.State}.");
                await Task.Delay(50);
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}

/// <summary>Задача, поставлена в чергу до перемикання режиму.</summary>
public sealed class DrainProbeJob : IBackgroundJob
{
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct) => Task.CompletedTask;
}
