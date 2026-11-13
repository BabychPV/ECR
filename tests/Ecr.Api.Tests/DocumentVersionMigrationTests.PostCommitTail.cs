// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.PostCommitTail.cs
using System.Collections.Concurrent;
using System.Net;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// F13 RC #872 (P2-1, P2-2) до V-5: хвіст переносу ПІСЛЯ коміту. Перерахунок (LP-41) і вʼюхи SSRS (V-5) ставляться
/// фоновими задачами — спершу перерахунок, потім <see cref="ReportViewsTask"/> цільової версії — і не залежать від
/// токена запиту: обрив з'єднання після коміту не пропускає перерахунку вже перенесеного проєкту.
/// </summary>
/// <remarks>
/// ⚠ Справжній HTTP і справжній SQL Server; підмінено лише планувальник (записує постановки й, як справжній,
/// поважає токен: скасований токен — <see cref="OperationCanceledException"/>) і шкалу версій (обгортка над справжньою:
/// щойно версія проєкту в базі вже цільова, тобто перенос закомічено, обриває HTTP-запит клієнта).
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F13-872-P2-2")]
    public async Task Request_aborted_after_commit_still_enqueues_the_recalculation()
    {
        var s = await ArrangeAsync(Target.AddsFormulaColumn).ConfigureAwait(true);
        var jobs = new TailRecordingScheduler();
        using var abort = new CancellationTokenSource();
        var probe = new AbortAfterCommitProbe(sql.ConnectionString, s.Doc.ProjectId, s.TargetVersionId, abort);

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Install(services, jobs, probe)));
        using var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        HttpStatusCode? status = null;
        try
        {
            using var response = await PostCancellableAsync(client, s, abort.Token).ConfigureAwait(true);
            status = response.StatusCode;
        }
        catch (OperationCanceledException)
        {
            // Очікувано: клієнт обірвав запит після коміту.
        }

        // Передумова: запит справді обірвано ПІСЛЯ коміту, і сервер це бачив (токен запиту скасовано).
        Assert.True(probe.Fired, $"обрив не стався (status {status}); {baseApp.ErrorsText}");
        Assert.True(probe.RequestAbortedObserved, "сервер не побачив скасування токена запиту — сценарій не відтворено");
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            Assert.Equal(
                s.TargetVersionId,
                await db.Projects.AsNoTracking().Where(p => p.Id == s.Doc.ProjectId).Select(p => p.TemplateVersionId).SingleAsync().ConfigureAwait(true));
        }

        // ⛔ Предмет тесту: перенос закомічено — перерахунок (нова формула) поставлено попри обрив.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !jobs.Calls.Any(c => c.JobType == typeof(IRecalculationJob)))
        {
            await Task.Delay(100).ConfigureAwait(true);
        }

        Assert.Contains(
            jobs.Calls,
            c => c.JobType == typeof(IRecalculationJob) && c.TargetKey == MigrationRecalculation.TargetOf(s.Doc.ProjectId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "V-5")]
    public async Task Column_rename_migration_enqueues_report_views_task_for_target_version_after_the_recalculation()
    {
        var s = await ArrangeAsync(Target.ColumnRenamedAddsFormula).ConfigureAwait(true);
        var jobs = new TailRecordingScheduler();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Install(services, jobs, probe: null)));
        using var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var response = await PostWithRenamesAsync(client, s, dryRun: false, ColumnOnlyRenames(s)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {baseApp.ErrorsText}");

        var calls = jobs.Calls.ToList();
        var recalc = calls.FindIndex(c => c.JobType == typeof(IRecalculationJob) && c.TargetKey == MigrationRecalculation.TargetOf(s.Doc.ProjectId));
        var views = calls.FindIndex(c => c.JobType == typeof(IReportViewsJob) && c.TargetKey == ReportViewsTarget.Of(s.Doc.TemplateId));
        var dump = string.Join("; ", calls.Select(c => $"{c.JobType.Name}@{c.TargetKey}"));
        Assert.True(recalc >= 0, "перерахунок не поставлено: " + dump);
        Assert.True(views >= 0, "задачу вʼюх не поставлено: " + dump);
        Assert.True(recalc < views, "вʼюхи поставлено ДО перерахунку: " + dump);

        var task = Assert.IsType<ReportViewsTask>(calls[views].Payload);
        Assert.Equal(s.Doc.TemplateId, task.TemplateId);
        Assert.Equal(s.TargetVersionId, task.TemplateVersionId);
        Assert.False(task.AllTemplates);
        Assert.Equal(ReportViewsTarget.Of(s.Doc.TemplateId), calls[views].TargetKey);
        Assert.Equal(s.UserId, calls[views].CreatedBy);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "V-5")]
    public async Task Migration_without_column_renames_does_not_enqueue_report_views()
    {
        // Контроль хибної постановки: перенос лише з новою формулою (без пар перейменувань колонок) вʼюх не чіпає.
        var s = await ArrangeAsync(Target.AddsFormulaColumn).ConfigureAwait(true);
        var jobs = new TailRecordingScheduler();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => Install(services, jobs, probe: null)));
        using var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var response = await PostAsync(client, s, "Safe", dryRun: false).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {baseApp.ErrorsText}");

        Assert.Contains(jobs.Calls, c => c.JobType == typeof(IRecalculationJob));
        // Старт застосунку ставить свою задачу «усі шаблони» (ціль `all`) — вона тут ні до чого; дивимось ціль шаблону.
        Assert.DoesNotContain(jobs.Calls, c => c.JobType == typeof(IReportViewsJob) && c.TargetKey == ReportViewsTarget.Of(s.Doc.TemplateId));
    }

    /// <summary>Мапа «та сама таблиця, C2 → C2X» (лише перейменування колонки).</summary>
    private static object[] ColumnOnlyRenames(Scenario s)
        =>
        [
            new
            {
                fromTableCode = s.SourceTableCode,
                toTableCode = s.SourceTableCode,
                sheetCode = (string?)null,
                columns = new[] { new { fromColumnCode = $"C2_{s.Tag}", toColumnCode = $"C2X_{s.Tag}" } },
            },
        ];

    private static void Install(IServiceCollection services, TailRecordingScheduler jobs, AbortAfterCommitProbe? probe)
    {
        services.AddScoped<IBackgroundJobScheduler>(_ => jobs);
        if (probe is null)
        {
            return;
        }

        // Обгортка над справжньою шкалою: та сама реєстрація, лише Invalidate після коміту обриває запит.
        var original = services.Last(d => d.ServiceType == typeof(IProjectVersionTimeline));
        services.RemoveAll<IProjectVersionTimeline>();
        services.AddScoped<IProjectVersionTimeline>(sp => new AbortingTimeline(
            (IProjectVersionTimeline)(original.ImplementationFactory is { } factory
                ? factory(sp)
                : ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)),
            probe,
            sp.GetRequiredService<IHttpContextAccessor>()));
    }

    /// <summary>Постановка задачі: тип, ціль, payload, автор.</summary>
    private sealed record TailCall(Type JobType, string? TargetKey, object? Payload, int? CreatedBy);

    /// <summary>
    /// Планувальник, що записує постановки по порядку. Як справжній, поважає токен: скасований — виняток, задачі немає.
    /// </summary>
    private sealed class TailRecordingScheduler : IBackgroundJobScheduler
    {
        private int _next;

        public ConcurrentQueue<TailCall> Calls { get; } = new();

        public Task<string> EnqueueAsync<TJob>(object? payload, CancellationToken ct, int? createdByUserId = null)
            where TJob : IBackgroundJob
            => Record<TJob>(null, payload, createdByUserId, ct);

        public Task<string> EnqueueExclusiveAsync<TJob>(
            string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
            where TJob : IBackgroundJob
            => Record<TJob>(targetKey, payload, createdByUserId, ct);

        public Task<string> EnqueueCoalescedAsync<TJob>(
            string targetKey, object? payload, CancellationToken ct, int? createdByUserId = null)
            where TJob : IBackgroundJob
            => Record<TJob>(targetKey, payload, createdByUserId, ct);

        public Task ScheduleAsync<TJob>(string cronExpression, object? payload, CancellationToken ct)
            where TJob : IBackgroundJob
            => Task.CompletedTask;

        public Task<bool> UnscheduleAsync<TJob>(object? payload, CancellationToken ct)
            where TJob : IBackgroundJob
            => Task.FromResult(false);

        public bool IsValidCron(string expression, out string? error)
        {
            error = null;
            return true;
        }

        public Task CancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> RestartAsync(string jobId, CancellationToken ct) => Task.FromResult(false);

        public Task<JobStatus> GetStatusAsync(string jobId, CancellationToken ct)
            => Task.FromResult(new JobStatus(jobId, "Queued", 0, null, null));

        public Task<int?> GetCreatedByUserIdAsync(string jobId, CancellationToken ct) => Task.FromResult<int?>(null);

        public Task<IReadOnlyList<JobSummary>> ListRecentAsync(JobListFilter filter, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JobSummary>>([]);

        private Task<string> Record<TJob>(string? targetKey, object? payload, int? createdBy, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Enqueue(new TailCall(typeof(TJob), targetKey, payload, createdBy));
            return Task.FromResult($"{typeof(TJob).Name}#{Interlocked.Increment(ref _next)}");
        }
    }

    /// <summary>Обриває HTTP-запит клієнта в момент, коли версія проєкту в базі вже цільова (перенос закомічено).</summary>
    private sealed class AbortAfterCommitProbe(string connectionString, int projectId, int targetVersionId, CancellationTokenSource abort)
    {
        public bool Fired { get; private set; }

        public bool RequestAbortedObserved { get; private set; }

        public void OnInvalidate(HttpContext? context)
        {
            if (Fired || !Committed())
            {
                return;
            }

            Fired = true;
            abort.Cancel();

            // Скасування з клієнта доходить до RequestAborted сервера; чекаємо, щоб хвіст ішов уже зі скасованим токеном.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && context is not null && !context.RequestAborted.IsCancellationRequested)
            {
                Thread.Sleep(20);
            }

            RequestAbortedObserved = context?.RequestAborted.IsCancellationRequested == true;
        }

        private bool Committed()
        {
            using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT TemplateVersionId FROM doc.Project WITH (READCOMMITTEDLOCK, READPAST) WHERE Id = @p";
            command.Parameters.AddWithValue("@p", projectId);
            return command.ExecuteScalar() is int version && version == targetVersionId;
        }
    }

    private sealed class AbortingTimeline(IProjectVersionTimeline inner, AbortAfterCommitProbe probe, IHttpContextAccessor http)
        : IProjectVersionTimeline
    {
        public Task<int> VersionOfAsync(int projectId, int periodKey, CancellationToken ct) => inner.VersionOfAsync(projectId, periodKey, ct);

        public Task<int> VersionOfFreshAsync(int projectId, int periodKey, CancellationToken ct) => inner.VersionOfFreshAsync(projectId, periodKey, ct);

        public Task<IReadOnlyList<TimelineEntry>> GetAsync(int projectId, CancellationToken ct) => inner.GetAsync(projectId, ct);

        public Task<int?> CurrentVersionAsync(int projectId, CancellationToken ct) => inner.CurrentVersionAsync(projectId, ct);

        public void Invalidate(int projectId)
        {
            inner.Invalidate(projectId);
            probe.OnInvalidate(http.HttpContext);
        }
    }

    /// <summary>POST переносу (Safe, застосування) з токеном скасування клієнта.</summary>
    private static Task<HttpResponseMessage> PostCancellableAsync(HttpClient client, Scenario s, CancellationToken ct)
        => System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(
            client,
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new { targetVersionId = s.TargetVersionId, mode = "Safe", dryRun = false },
            ct);
}
