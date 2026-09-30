// tests/Ecr.Worker.Tests/ChildWorkerFailoverTests.cs

using Ecr.Application.Ports;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Ecr.Worker.Child;
using Ecr.Worker.Isolation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ecr.Worker.Tests;

/// <summary>
/// ФВ-9.8 (D-206, I1): дочірній зник посеред перерахунку — після <c>LeaseUntil</c>
/// задачу підхоплює інший, і актуальним лишається РІВНО ОДИН прогін.
/// </summary>
/// <remarks>
/// ⚠ Рівень — два виконавці зі СПРАВЖНІМ складанням дочірнього
/// (<see cref="ChildComposition"/>) і справжньою задачею перерахунку, а не два
/// процеси. Причини: (1) у дочірньому лише бойові задачі, а перерахунок
/// тестового документа триває мілісекунди — «вбити посеред задачі» процес
/// не вдається детерміновано; (2) інший процес перехоплює задачу лише після
/// оренди (2 хв, у дочірньому не налаштовується) — тест тривав би хвилини.
/// «Смерть» A тут — рівно те, що бачить черга від убитого процесу: оренда
/// захоплена й більше не подовжується; прострочення — зсувом <c>LeaseUntil</c>.
/// <para>
/// Мутація: прибрати <c>FenceAsync</c> у <c>RunCalculationHandler.CompleteAsync</c> —
/// пізнє завершення A робить актуальним його прогін, тест червоний.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage8)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "ФВ-9.8")]
public sealed class ChildWorkerFailoverTests(SqlServerFixture sql) : ChildWorkerTestsBase(sql)
{
    [Fact]
    public async Task Зниклий_дочірній_інший_підхоплює_після_оренди_актуальний_рівно_один_прогін()
    {
        var document = await DocumentAsync();
        var jobId = await EnqueueAsync<IRecalculationJob>(RecalcOf(document));

        // A: захопив задачу й «помер» — оренду більше ніхто не подовжує.
        await using var hostA = ChildHost();
        await using var scopeA = hostA.CreateAsyncScope();
        var claimA = await scopeA.ServiceProvider.GetRequiredService<IJobQueue>().ClaimAsync(
            [JobLanes.Recalc],
            JobProgressStore.InstanceIdOf(JobProgressStore.CurrentMachineName, JobProgressStore.RoleWorker, Guid.NewGuid()),
            JobQueueLimits.DefaultLease,
            CancellationToken.None);
        Assert.Equal(jobId, claimA?.Claim.JobId);
        scopeA.ServiceProvider.GetRequiredService<JobLeaseContext>().Bind(claimA!.Claim);

        await ExecAsync("UPDATE itg.JobProgress SET LeaseUntil = DATEADD(second, -5, SYSUTCDATETIME()) WHERE JobId = @id;", jobId);

        // B: інший дочірній (те саме складання) підхоплює й доводить до кінця.
        await using (var hostB = ChildHost())
        {
            var worker = new JobWorker(
                hostB.GetRequiredService<IServiceScopeFactory>(),
                ChildComposition.WorkerOptions(new WorkerPoolOptions()) with
                {
                    // ⚠ Роль процесу тестів не змінюється: її зафіксовано першим читанням.
                    Role = JobProgressStore.CurrentRole,
                    PollInterval = TimeSpan.FromMilliseconds(100),
                },
                hostB.GetRequiredService<JobQueueSignal>(),
                NullLogger<JobWorker>.Instance);

            await worker.StartAsync(CancellationToken.None);
            try
            {
                await WaitForStateAsync(jobId, "Succeeded");
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
                worker.Dispose();
            }
        }

        var afterB = Assert.Single(await RunsAsync(document.ProjectId));
        Assert.True(afterB.IsCurrent);

        // A «прокидається» і доробляє той самий перерахунок зі своєю (вже чужою) орендою.
        var lost = await Record.ExceptionAsync(() => scopeA.ServiceProvider.GetRequiredService<IRecalculationJob>()
            .ExecuteAsync(claimA.PayloadJson, NoProgress.Instance, CancellationToken.None));

        // ⛔ Головне твердження — видимість: актуальний рівно один прогін, і це прогін B.
        var runs = await RunsAsync(document.ProjectId);
        var current = Assert.Single(runs, r => r.IsCurrent);
        Assert.Equal(afterB.Id, current.Id);
        Assert.IsType<JobLeaseLostException>(lost);
        Assert.Equal(2, runs.Count);
        Assert.Equal("Failed", runs.Single(r => r.Id != afterB.Id).Status);
    }

    /// <summary>«Процес» дочірнього: власний контейнер зі складанням <see cref="ChildComposition"/>.</summary>
    private ServiceProvider ChildHost()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ecr"] = Sql.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        ChildComposition.AddChildWorker(services, configuration, new WorkerPoolOptions());
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class NoProgress : IJobProgress
    {
        public static readonly NoProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
