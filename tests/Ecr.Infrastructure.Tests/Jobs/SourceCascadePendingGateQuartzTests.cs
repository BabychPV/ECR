// tests/Ecr.Infrastructure.Tests/Jobs/SourceCascadePendingGateQuartzTests.cs
using System.Collections.Specialized;
using System.Globalization;
using Ecr.Application.Calculations;
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Binding;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Quartz;
using Quartz.Impl;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ D-567 (4), b10: F5 і для Quartz у пам'яті (<c>D-563</c>: дозволений малим інсталяціям; live — Quartz/InProcess).
/// Задачі ставить СПРАВЖНІЙ <see cref="QuartzJobScheduler"/> (не запущений — задача чекає) зі справжнім
/// <see cref="JobProgressStore"/>: рядок дзеркала без цілі й payload, <c>JobId = {тип}~{ціль}~{guid}</c>. Ворота бачать
/// каскад споживача, власний перерахунок джерела (<c>-auto</c>, період — з цілі) і задачу впливу зв'язки; скасована — ні.
/// </summary>
/// <remarks>
/// ⛔ ДОКАЗ (локально, не в коміті): прибрати з <c>SourceCascadePendingGate</c> обчислення цілі з <c>JobId</c>
/// (<c>EffectiveTarget</c> → <c>q.TargetKey</c>) — червоні всі три «тримає».
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Requirement", "F5")]
[Trait("Decision", "D-567")]
public sealed class SourceCascadePendingGateQuartzTests(SqlServerFixture sql)
{
    private static int _binding = 1_800_000_000 - (Environment.ProcessId % 100_000 * 100);

    [Fact]
    public async Task Каскад_споживача_у_Quartz_тримає_місяць_а_скасований_ні()
    {
        var w = await ArrangeAsync();
        await using var q = await QuartzAsync();

        Assert.False(await PendingAsync(w, w.Consumer));

        var jobId = (await Trigger(q.Jobs).RequestDependentsAsync(w.Consumer.DocumentId, w.Consumer.PeriodKey, CancellationToken.None))!;
        Assert.StartsWith(
            $"{nameof(IRecalculationJob)}~{CalculationTrigger.DependentsTargetOf(w.Consumer.DocumentId, w.Consumer.PeriodKey)}~",
            jobId,
            StringComparison.Ordinal);
        var row = await RowAsync(jobId);
        Assert.Equal(("Queued", (string?)null, (string?)null), row);

        Assert.True(await PendingAsync(w, w.Consumer));
        Assert.False(await PendingAsync(w, w.Other)); // парно: чужий документ

        await q.Jobs.CancelAsync(jobId, CancellationToken.None);
        Assert.Equal("Cancelled", (await RowAsync(jobId)).State);
        Assert.False(await PendingAsync(w, w.Consumer));
    }

    /// <summary>
    /// Той самий шлях, що репетиція W15z (`f5-natural-w15z.ps1`): правка джерела → хук → <c>RequestSourceCascadeAsync</c>; у
    /// Quartz черга періодів не об'єднує, тож рядок дзеркала — <c>IRecalculationJob~doc{id}-p{період}-dependents~{guid}</c>
    /// без TargetKey. Місяць чекає і поки задача чекає (Queued), і поки виконується (Running — ті самі виклики
    /// <see cref="JobProgressStore"/>, що робить <c>QuartzJobAdapter</c>); завершена — ні.
    /// </summary>
    [Fact]
    public async Task Каскад_від_правки_джерела_у_Quartz_тримає_місяць_у_Queued_і_Running()
    {
        var w = await ArrangeAsync();
        await using var q = await QuartzAsync();
        var trigger = Trigger(q.Jobs);
        Assert.False(trigger.CoalescesCascadePeriods);

        var enqueued = await trigger.RequestSourceCascadeAsync(
            w.Consumer.DocumentId, w.Year, [w.Consumer.PeriodKey.Value], 1, CancellationToken.None);
        var jobId = enqueued.JobId!;
        Assert.Equal([w.Consumer.PeriodKey.Value], enqueued.Periods);
        Assert.StartsWith(
            $"{nameof(IRecalculationJob)}~doc{w.Consumer.DocumentId.ToString(CultureInfo.InvariantCulture)}-p{w.Consumer.PeriodKey.Value.ToString(CultureInfo.InvariantCulture)}-dependents~",
            jobId,
            StringComparison.Ordinal);
        Assert.Equal(("Queued", (string?)null, (string?)null), await RowAsync(jobId));
        Assert.True(await PendingAsync(w, w.Consumer));

        await using (var db = Context())
        {
            var store = new JobProgressStore(db);
            await store.StartAsync(jobId, JobListKinds.RecalculationJobCode, new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc), CancellationToken.None);
            Assert.Equal("Running", (await RowAsync(jobId)).State);
            Assert.True(await PendingAsync(w, w.Consumer));

            await store.FinishAsync(jobId, "Succeeded", null, new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        }

        Assert.False(await PendingAsync(w, w.Consumer));
    }

    [Fact]
    public async Task Власний_перерахунок_джерела_у_Quartz_тримає_споживача_а_не_споживача_ні()
    {
        var w = await ArrangeAsync();
        await using var q = await QuartzAsync();

        var jobId = await q.Jobs.EnqueueCoalescedAsync<IRecalculationJob>(
            CalculationTrigger.AutoTargetOf(w.Source.DocumentId, w.Source.PeriodKey),
            new { ProjectId = 0, DocumentId = w.Source.DocumentId, PeriodKey = w.Source.PeriodKey.Value },
            CancellationToken.None);
        try
        {
            Assert.True(await PendingAsync(w, w.Consumer));
            Assert.False(await PendingAsync(w, w.Other));
            Assert.False(await PendingAsync(w, w.Consumer, autoBindings: [])); // зв'язка каскаду з -auto не ставить
        }
        finally
        {
            await q.Jobs.CancelAsync(jobId, CancellationToken.None);
        }

        Assert.False(await PendingAsync(w, w.Consumer));
    }

    [Fact]
    public async Task Задача_впливу_зв_язки_у_Quartz_тримає_споживача()
    {
        var w = await ArrangeAsync();
        await using var q = await QuartzAsync();

        var jobId = await q.Jobs.EnqueueCoalescedAsync<IRegistryImpactRecalculationJob>(
            RegistryImpactRecalculationTarget.ForSourceChange(w.BindingId, w.Year, 3),
            new { BindingId = w.BindingId, Year = w.Year, Month = 3 },
            CancellationToken.None);
        try
        {
            Assert.True(await PendingAsync(w, w.Consumer));
            Assert.False(await PendingAsync(w, w.Other));
        }
        finally
        {
            await q.Jobs.CancelAsync(jobId, CancellationToken.None);
        }

        Assert.False(await PendingAsync(w, w.Consumer));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private sealed record World(TestDocument Consumer, TestDocument Other, TestDocument Source, int BindingId, int ConsumerTemplateId, int SourceTemplateId, int Year);

    private sealed class QuartzHost(QuartzJobScheduler jobs, IScheduler scheduler, EcrDbContext db) : IAsyncDisposable
    {
        public QuartzJobScheduler Jobs { get; } = jobs;

        public async ValueTask DisposeAsync()
        {
            await scheduler.Shutdown(waitForJobsToComplete: false);
            await db.DisposeAsync();
        }
    }

    private async Task<QuartzHost> QuartzAsync()
    {
        // ⚠ Не запущений: задача лишається тією, що чекає (Queued), — як у вікні до старту.
        var factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"ecr-f5-quartz-{Guid.NewGuid():N}",
            ["quartz.threadPool.threadCount"] = "1",
        });
        var scheduler = await factory.GetScheduler();
        var db = Context();
        return new QuartzHost(new QuartzJobScheduler(factory, new JobProgressStore(db), new SystemClock()), scheduler, db);
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static CalculationTrigger Trigger(IBackgroundJobScheduler jobs)
    {
        var periods = Substitute.For<IPeriodStore>();
        periods.FindPeriodStateAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(PeriodState.Open);
        var workflow = Substitute.For<IWorkflowStore>();
        workflow.GetSheetsAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<ApprovalState>());
        var documents = Substitute.For<IDocumentStore>();
        documents.GetIncludedSheetIdsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());
        var sheetCopies = Substitute.For<IDocumentSheetCopyStore>();
        sheetCopies.ListGroupsAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<SheetCopyGroupInfo>());
        return new CalculationTrigger(jobs, periods, workflow, documents, sheetCopies);
    }

    private async Task<World> ArrangeAsync()
    {
        var consumer = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync();
        var other = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(consumer.PeriodKey.Value);
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(consumer.PeriodKey.Value);
        return new World(
            consumer, other, source, Interlocked.Decrement(ref _binding), await TemplateOfAsync(consumer), await TemplateOfAsync(source),
            consumer.PeriodKey.Value / 100);
    }

    private async Task<bool> PendingAsync(World w, TestDocument document, IReadOnlyCollection<int>? autoBindings = null)
    {
        var graph = Substitute.For<IDependencyGraphProvider>();
        graph.GetAsync(Arg.Any<CancellationToken>()).Returns(new DependencyGraphSnapshot(
            "f5q", [], [],
            [new TemplateDependency(w.SourceTemplateId, w.ConsumerTemplateId, w.BindingId, 1, new PeriodScope(PeriodScopeKind.Year, 0), true, false)],
            [], [], []));

        await using var db = Context();
        var bindings = autoBindings ?? [w.BindingId];
        var gate = new SourceCascadePendingGate(db, gateCache: null, graph) { AutoBindingsProbe = _ => Task.FromResult(bindings) };
        return await gate.IsPendingAsync(document.DocumentId, document.PeriodKey.Value, CancellationToken.None);
    }

    private async Task<(string State, string? TargetKey, string? Payload)> RowAsync(string jobId)
    {
        await using var db = Context();
        var row = await db.JobProgresses.AsNoTracking().SingleAsync(p => p.JobId == jobId);
        return (row.State, row.TargetKey, row.Payload);
    }

    private async Task<int> TemplateOfAsync(TestDocument document)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(d.TemplateId, v.TemplateId)
              FROM doc.Document AS d
              LEFT JOIN cfg.TemplateVersion AS v ON v.Id = CASE WHEN d.TemplateId IS NULL THEN doc.fn_VersionOf(d.ProjectId, @period) ELSE doc.fn_TemplateVersionOf(d.ProjectId, d.TemplateId, @period) END
             WHERE d.Id = @doc;
            """;
        command.Parameters.AddWithValue("@doc", document.DocumentId);
        command.Parameters.AddWithValue("@period", document.PeriodKey.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
