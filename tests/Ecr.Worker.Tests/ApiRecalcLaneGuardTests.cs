// tests/Ecr.Worker.Tests/ApiRecalcLaneGuardTests.cs

using Ecr.Application;
using Ecr.Application.Ports;
using Ecr.Calculations;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Ecr.Worker.Child;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Worker.Tests;

/// <summary>
/// Сторож «Api не бере recalc при <c>Jobs:Recalculation:Executor = Worker</c>»
/// (D-206, правка «Аудиту» 3) — поведінкою на справжній черзі, зі справжнім
/// <c>Ecr.Worker --child</c>, а не лише реєстрацією (<c>JobLaneTests</c>).
/// </summary>
/// <remarks>
/// Мутація: <c>JobLaneMap.ApiLanes</c> завжди <c>JobLanes.All</c> — Api бере recalc
/// за перше ж опитування, тест червоний.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class ApiRecalcLaneGuardTests(SqlServerFixture sql) : ChildWorkerTestsBase(sql)
{
    [Fact]
    public async Task Api_з_виконавцем_Worker_не_бере_перерахунок_а_default_бере()
    {
        var document = await DocumentAsync();
        var recalc = await EnqueueAsync<IRecalculationJob>(RecalcOf(document));

        await using var api = ApiHost("Worker");

        // ⚠ Виконавець — з реєстрації Api (його JobWorkerOptions, тобто ApiLanes), а
        // не зібраний тестом: сторожиться саме те, що отримає Api. Решта фонових
        // служб (Quartz) не стартує — хоста тут немає.
        using var worker = ActivatorUtilities.CreateInstance<JobWorker>(api);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Той самий воркер Api живий і бере свій лейн — інакше «не взяв recalc» нічого б не доводило.
            var probe = await EnqueueAsync<ApiLaneProbeJob>(new { n = 1 });
            var probeRow = await WaitForStateAsync(probe, "Succeeded");
            Assert.Equal(JobLanes.Default, probeRow.Lane);

            // ⛔ За кілька опитувань (раз на секунду) recalc так і лишився в черзі.
            await Task.Delay(TimeSpan.FromSeconds(3));
            var row = await RowAsync(recalc);
            Assert.Equal("Queued", row?.State);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // Його бере пул — справжній дочірній процес.
        using var child = StartChild();
        var done = await WaitForStateAsync(recalc, "Succeeded", child.Tail);
        Assert.Contains($"/{JobProgressStore.RoleWorker}/", done.InstanceId, StringComparison.Ordinal);
    }

    /// <summary>Складання Api (без веб-частини): Infrastructure з <c>Jobs:Queue:Mode = Database</c>.</summary>
    private ServiceProvider ApiHost(string executor)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Ecr"] = Sql.ConnectionString,
                [DbBackgroundJobScheduler.ModeKey] = nameof(JobQueueMode.Database),
                [JobLaneMap.ExecutorKey] = executor,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddEcrInfrastructure(configuration);
        services.AddEcrCalculations();
        services.AddEcrApplication();
        services.AddSingleton<NoRequestCurrentUser>();
        services.AddEcrJobActor<NoRequestCurrentUser>();
        services.AddScoped<ApiLaneProbeJob>();

        // Передумова: Api саме з цією конфігурацією реєструє свій виконавець черги.
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(JobWorker));
        return services.BuildServiceProvider();
    }
}

/// <summary>Задача лейну default: доказ, що воркер Api живий і опитує чергу.</summary>
public sealed class ApiLaneProbeJob : IBackgroundJob
{
    public Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct) => Task.CompletedTask;
}
