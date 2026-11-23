// tests/Ecr.Infrastructure.Tests/Jobs/ApprovedDependentsFallbackTests.cs
using System.Collections.Concurrent;
using System.Diagnostics;
using Ecr.Application.Calculations;
using Ecr.Application.Documents;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B80 N5 / перф-аудит 20.11: «залежні з затвердженими» ПОКРИВАЄ звичайний перерахунок залежних того самого документа
/// й періоду (звичайна не ставиться чи скасовується). Покривна закінчилась НЕ успіхом — провал, скасування людиною
/// (що чекала чи виконувану), системне закриття — і покрита робота повертається звичайним шляхом тригера.
/// </summary>
/// <remarks>
/// ⛔ ДОКАЗ (локально): у <c>ApprovedDependentsFallback.OnEndedAsync</c> не кликати тригер (повертати порожньо) — червоні
/// всі тести «…повертає_звичайну…»; прибрати виклик <c>ReturnCoveredAsync</c> з <c>JobWorker.RunAsync</c> — червоні
/// <c>Провалена_…</c> і <c>Скасована_людиною_виконувана_…</c>; прибрати виклик з <c>DbBackgroundJobScheduler.CancelAsync</c> —
/// червоний <c>Скасована_людиною_що_чекала_…</c>.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PERF-DEPENDENTS-DUP")]
public sealed class ApprovedDependentsFallbackTests(SqlServerFixture sql) : DbJobQueueTestsBase(sql)
{
    private static readonly PeriodKey Period = new(202601);

    private static readonly ApprovedRecalculationOverride AdminOverride = new(9, "Акт 12");

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static string RegularTarget(long documentId)
        => $"{nameof(IRecalculationJob)}~{CalculationTrigger.DependentsTargetOf(documentId, Period)}";

    [Fact]
    public async Task Провалена_затверджена_повертає_звичайну_яку_покривала()
    {
        const long document = 7901;
        var script = new RecalcScript { ApprovedFails = true };
        await using var host = Build(script);

        // Виконавець — ПІСЛЯ постановок: інакше він міг би взяти затверджену раніше, ніж звичайна встигла б покритися.
        var (approved, covered) = await ApprovedCoveringRegularAsync(host, document);
        await host.StartWorkerAsync();
        await WaitForStateAsync(approved, "Failed");

        // Повернута звичайна — НОВА задача на ціль звичайних, і виконавець її виконує.
        var regular = await WaitForRegularAsync(document, "Succeeded");
        Assert.NotEqual(covered, regular.JobId);
        Assert.Contains(script.Runs, r => !r.Approved);
    }

    [Fact]
    public async Task Скасована_людиною_виконувана_затверджена_повертає_звичайну()
    {
        const long document = 7902;
        var script = new RecalcScript { ApprovedFails = false };
        await using var host = Build(script);

        var (approved, _) = await ApprovedCoveringRegularAsync(host, document);
        await host.StartWorkerAsync();
        await script.ApprovedStarted.Task.WaitAsync(Patience);

        await using (var scope = host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>().CancelAsync(approved, CancellationToken.None);
        }

        await WaitForStateAsync(approved, "Cancelled");
        await WaitForRegularAsync(document, "Succeeded");
    }

    [Fact]
    public async Task Скасована_людиною_що_чекала_затверджена_повертає_звичайну()
    {
        const long document = 7903;
        await using var host = Build(new RecalcScript());

        var (approved, covered) = await ApprovedCoveringRegularAsync(host, document);

        await using (var scope = host.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>().CancelAsync(approved, CancellationToken.None);
        }

        Assert.Equal("Cancelled", (await RowAsync(approved))!.State);
        var regular = await WaitForRegularAsync(document, "Queued");
        Assert.NotEqual(covered, regular.JobId);
    }

    /// <summary>Системне закриття: власник помер, запит скасування лишився — <c>ExpireEndedAsync</c> → повернення.</summary>
    [Fact]
    public async Task Закрита_системою_після_смерті_власника_затверджена_повертає_звичайну()
    {
        const long document = 7904;
        await using var host = Build(new RecalcScript());
        var (approved, _) = await ApprovedCoveringRegularAsync(host, document);

        await using var dead = NewHost();
        var claim = await dead.Queue.ClaimAsync([JobLanes.Recalc], "host/dead", JobQueueLimits.DefaultLease, CancellationToken.None);
        Assert.Equal(approved, claim?.Claim.JobId);
        await dead.Queue.RequestCancelAsync(approved, CancellationToken.None);
        await ExpireLeaseAsync(approved);

        await using var scope = host.CreateAsyncScope();
        var ended = await scope.ServiceProvider.GetRequiredService<IJobQueue>()
            .ExpireEndedAsync(JobQueueLimits.MaxReclaims, CancellationToken.None);
        var returned = await scope.ServiceProvider.GetRequiredService<ApprovedDependentsFallback>()
            .OnEndedAsync(ended, CancellationToken.None);

        Assert.Equal("Cancelled", (await RowAsync(approved))!.State);
        var regular = await WaitForRegularAsync(document, "Queued");
        Assert.Equal([regular.JobId], returned);
    }

    /// <summary>
    /// Зайвий виклик безпечний: успішна затверджена нічого не повертає, а провалена, поки на ту саму ціль уже чекає
    /// інша «з затвердженими» (перепостановка після стелі відкладень), — повертає ту, що чекає (покриття), без нової.
    /// </summary>
    [Fact]
    public async Task Успішна_нічого_не_повертає_а_провалена_за_чекаючої_затвердженої_покривається_нею()
    {
        const long document = 7905;
        await using var host = Build(new RecalcScript());
        await using var worker = NewHost();
        await using var scope = host.CreateAsyncScope();
        var trigger = scope.ServiceProvider.GetRequiredService<ICalculationTrigger>();
        var fallback = scope.ServiceProvider.GetRequiredService<ApprovedDependentsFallback>();

        var first = (await trigger.RequestDependentsAsync(document, Period, 1, AdminOverride, CancellationToken.None))!;
        var claim = await worker.Queue.ClaimAsync([JobLanes.Recalc], "host/w", JobQueueLimits.DefaultLease, CancellationToken.None);
        Assert.True(await worker.Queue.CompleteAsync(claim!.Claim, CancellationToken.None));
        Assert.Empty(await fallback.OnEndedAsync([first], CancellationToken.None));

        var second = (await trigger.RequestDependentsAsync(document, Period, 1, AdminOverride, CancellationToken.None))!;
        claim = await worker.Queue.ClaimAsync([JobLanes.Recalc], "host/w", JobQueueLimits.DefaultLease, CancellationToken.None);
        var behind = (await trigger.RequestDependentsAsync(document, Period, 1, AdminOverride, CancellationToken.None))!;
        Assert.True(await worker.Queue.FailAsync(claim!.Claim, "boom", null, CancellationToken.None));

        Assert.Equal([behind], await fallback.OnEndedAsync([second], CancellationToken.None));
        await using var db = Sql.CreateContext();
        Assert.Equal(0, await db.JobProgresses.CountAsync(p => p.TargetKey == RegularTarget(document)));
    }

    /// <summary>
    /// ✎ B80-N5f: «з затвердженими» без цілі черги (рядок без <c>TargetKey</c> — поставлений без цілі планувальником)
    /// провалилась — покрита робота все одно повертається: документ і період — з payload (<c>IncludeApproved</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): повернути <c>p.TargetKey != null</c> у відбір <c>OnEndedAsync</c> чи <c>targetKey is not null &amp;&amp;</c>
    /// в <c>IsCandidate</c> — червоний (звичайної немає).
    /// </remarks>
    [Fact]
    [Trait("Finding", "B80-N5f")]
    public async Task Провалена_затверджена_без_цілі_черги_повертає_звичайну()
    {
        const long document = 7906;
        var script = new RecalcScript { ApprovedFails = true };
        await using var host = Build(script);

        string approved;
        await using (var scope = host.CreateAsyncScope())
        {
            // Той самий payload, що ставить CalculationTrigger для «з затвердженими», — справжнім планувальником, без цілі.
            approved = await scope.ServiceProvider.GetRequiredService<IBackgroundJobScheduler>().EnqueueAsync<IRecalculationJob>(
                new
                {
                    DocumentId = document,
                    PeriodKey = Period.Value,
                    TriggeredByUserId = (int?)null,
                    SheetDefId = (int?)null,
                    RecalculateSubmitted = true,
                    SourceHop = 1,
                    IncludeApproved = true,
                    OverrideUserId = (int?)AdminOverride.UserId,
                    OverrideReason = AdminOverride.Reason,
                },
                CancellationToken.None);
        }

        var stored = (await RowAsync(approved))!;
        Assert.Null(stored.TargetKey);
        Assert.Equal(JobListKinds.RecalculationJobCode, stored.JobCode);

        await host.StartWorkerAsync();
        await WaitForStateAsync(approved, "Failed");
        var regular = await WaitForRegularAsync(document, "Succeeded");
        Assert.StartsWith($"{nameof(IRecalculationJob)}~", regular.TargetKey, StringComparison.Ordinal);
    }

    /// <summary>
    /// ✎ B80-N5f: ретрай «з затвердженими», поки позаду на ту саму ціль уже чекає інша, — рядок ПОГЛИНАЄТЬСЯ (Cancelled) і
    /// ціль у збереженій формі <c>{JobType}~{ціль}</c> лишається (зняття цілі в <c>DbJobQueue</c> — лише для задачі формул
    /// з масивом злиття); повернення віддає ту, що чекає (покриття), звичайної не з'являється.
    /// </summary>
    [Fact]
    [Trait("Finding", "B80-N5f")]
    public async Task Ретрай_затвердженої_за_чекаючої_лишає_ціль_і_повертає_покриття()
    {
        const long document = 7907;
        await using var host = Build(new RecalcScript());
        await using var worker = NewHost();
        await using var scope = host.CreateAsyncScope();
        var trigger = scope.ServiceProvider.GetRequiredService<ICalculationTrigger>();
        var fallback = scope.ServiceProvider.GetRequiredService<ApprovedDependentsFallback>();
        var approvedTarget = $"{nameof(IRecalculationJob)}~{CalculationTrigger.DependentsApprovedTargetOf(document, Period)}";

        var first = (await trigger.RequestDependentsAsync(document, Period, 1, AdminOverride, CancellationToken.None))!;
        var claim = await worker.Queue.ClaimAsync([JobLanes.Recalc], "host/w", JobQueueLimits.DefaultLease, CancellationToken.None);
        Assert.Equal(first, claim!.Claim.JobId);
        var behind = (await trigger.RequestDependentsAsync(document, Period, 1, AdminOverride, CancellationToken.None))!;
        Assert.True(await worker.Queue.RequeueAsync(claim.Claim, TimeSpan.Zero, CancellationToken.None));

        var row = (await RowAsync(first))!;
        Assert.Equal("Cancelled", row.State);
        Assert.Equal(approvedTarget, row.TargetKey);
        Assert.Equal(approvedTarget, (await RowAsync(behind))!.TargetKey);

        Assert.Equal([behind], await fallback.OnEndedAsync([first], CancellationToken.None));
        await using var db = Sql.CreateContext();
        Assert.Equal(0, await db.JobProgresses.CountAsync(p => p.TargetKey == RegularTarget(document)));
    }

    /// <summary>Затверджена стоїть у черзі, звичайна прийшла після неї й покрита (не поставлена).</summary>
    private static async Task<(string Approved, string Covered)> ApprovedCoveringRegularAsync(ServiceProvider host, long document)
    {
        await using var scope = host.CreateAsyncScope();
        var trigger = scope.ServiceProvider.GetRequiredService<ICalculationTrigger>();
        var approved = await trigger.RequestDependentsAsync(document, Period, 1, AdminOverride, CancellationToken.None);
        var covered = await trigger.RequestDependentsAsync(document, Period, 1, CancellationToken.None);
        Assert.NotNull(approved);
        Assert.Equal(approved, covered);
        return (approved!, covered!);
    }

    private async Task<Ecr.Domain.Entities.Integration.JobProgress> WaitForRegularAsync(long document, string state)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            await using (var db = Sql.CreateContext())
            {
                var row = await db.JobProgresses.AsNoTracking()
                    .Where(p => p.TargetKey == RegularTarget(document))
                    .OrderByDescending(p => p.CreatedAt)
                    .FirstOrDefaultAsync();
                if (row?.State == state)
                {
                    return row;
                }

                Assert.True(clock.Elapsed < Patience, $"звичайна {document}: очікувався {state}, стан {row?.State ?? "—"}.");
            }

            await Task.Delay(50);
        }
    }

    private async Task WaitForStateAsync(string jobId, string state)
    {
        var clock = Stopwatch.StartNew();
        while ((await RowAsync(jobId))?.State != state)
        {
            Assert.True(clock.Elapsed < Patience, $"{jobId}: очікувався {state}.");
            await Task.Delay(50);
        }
    }

    private FallbackHost Build(RecalcScript script)
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
        services.AddScoped<IBackgroundJobScheduler>(sp => sp.GetRequiredService<DbBackgroundJobScheduler>());
        services.AddScoped<ICalculationTrigger>(sp => Trigger(sp.GetRequiredService<IBackgroundJobScheduler>()));
        services.AddScoped<ApprovedDependentsFallback>();
        services.AddSingleton(script);
        services.AddScoped<IRecalculationJob, ScriptedRecalculationJob>();
        return new FallbackHost(services.BuildServiceProvider());
    }

    private static CalculationTrigger Trigger(IBackgroundJobScheduler jobs)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(PeriodState.Open);

        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetSheetsAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ApprovalState>());

        var documents = Substitute.For<IDocumentStore>();
        documents.GetIncludedSheetIdsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var sheetCopies = Substitute.For<IDocumentSheetCopyStore>();
        sheetCopies.ListGroupsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SheetCopyGroupInfo>());

        return new CalculationTrigger(jobs, periods, workflow, documents, sheetCopies);
    }

    private sealed class FallbackHost(ServiceProvider provider) : IAsyncDisposable
    {
        private JobWorker? worker;

        public static implicit operator ServiceProvider(FallbackHost host) => host.Provider;

        public ServiceProvider Provider { get; } = provider;

        public AsyncServiceScope CreateAsyncScope() => Provider.CreateAsyncScope();

        public async Task StartWorkerAsync()
        {
            worker = new JobWorker(
                Provider.GetRequiredService<IServiceScopeFactory>(),
                new JobWorkerOptions
                {
                    Lanes = JobLanes.All,
                    Role = JobProgressStore.CurrentRole,
                    PollInterval = TimeSpan.FromMilliseconds(50),
                    RenewInterval = TimeSpan.FromMilliseconds(100),
                    RetryDelay = n => TimeSpan.FromMilliseconds(50 * n),
                    SettleRetryDelay = _ => TimeSpan.FromMilliseconds(50),
                },
                Provider.GetRequiredService<JobQueueSignal>(),
                NullLogger<JobWorker>.Instance);
            await worker.StartAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (worker is not null)
            {
                await worker.StopAsync(CancellationToken.None);
                worker.Dispose();
            }

            await Provider.DisposeAsync();
        }
    }

    /// <summary>Що робить підставний перерахунок: звичайна — успіх одразу; «з затвердженими» — провал чи чекання скасування.</summary>
    public sealed class RecalcScript
    {
        public bool ApprovedFails { get; init; }

        public ConcurrentQueue<(bool Approved, string Payload)> Runs { get; } = new();

        public TaskCompletionSource ApprovedStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Підставний <see cref="IRecalculationJob"/> за сценарієм <see cref="RecalcScript"/>.</summary>
    public sealed class ScriptedRecalculationJob(RecalcScript script) : IRecalculationJob
    {
        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            var json = payload as string ?? string.Empty;
            var approved = json.Contains("\"includeApproved\":true", StringComparison.Ordinal);
            script.Runs.Enqueue((approved, json));
            if (!approved)
            {
                return;
            }

            if (script.ApprovedFails)
            {
                // Вердикт (не ретраїться): одразу Failed.
                throw new NotFoundException("ECR-TEST-0404", "Підставний провал перерахунку «з затвердженими».");
            }

            script.ApprovedStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
    }
}
