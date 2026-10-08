// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.BackgroundProgress.cs
using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-2 (RC15B): прогрес фонового переносу — стадії всередині транзакції видно опитувачу ДО коміту.
/// </summary>
/// <remarks>
/// ⛔ Канал прогресу самої задачі ділить <c>EcrDbContext</c> з транзакцією переносу: запис кроку лягав би в неї,
/// був би невидимий опитувачу до коміту і відкотився б разом із нею. Тому задача пише прогрес окремим скоупом.
/// Тест ловить саме це: поки перенос стоїть усередині транзакції, <c>GET /jobs</c> має показати крок стадії.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Прогрес_стадії_видно_опитувачу_поки_перенос_ще_у_транзакції()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var pause = new ApplyPause();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVersionMigrationStore>(sp => new ObservingMigrationStore(
                new DocumentVersionMigrationStore(sp.GetRequiredService<EcrDbContext>()),
                async (inner, projectId, targetVersionId, plan, onStep, ct) =>
                {
                    // Крок 3 з 12, 5000 рядків змінено: стадія значень комірок.
                    await onStep!(VersionMigrationStages.Cells, 3, 12, 5000, ct).ConfigureAwait(false);
                    pause.Reached.TrySetResult();
                    await pause.Release.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
                    await inner.ApplyAsync(projectId, targetVersionId, plan, onStep, ct).ConfigureAwait(false);
                }))));

        using var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var started = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, started.StatusCode);
        var jobId = JsonDocument.Parse(await started.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("jobId").GetString()!;
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);

        // ⛔ Предмет тесту: транзакція ще відкрита, а стан уже показує стадію й відсоток вище за «запущено».
        JsonElement seen = default;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            seen = await client.GetFromJsonAsyncJob(jobId).ConfigureAwait(true);
            if (seen.GetProperty("percent").GetInt32() > 15)
            {
                break;
            }

            await Task.Delay(100).ConfigureAwait(true);
        }

        Assert.Equal("Running", seen.GetProperty("state").GetString());
        Assert.InRange(seen.GetProperty("percent").GetInt32(), 16, 96);
        Assert.Contains("Moving cell values", seen.GetProperty("message").GetString(), StringComparison.Ordinal);

        pause.Release.TrySetResult();
        var job = await WaitJobAsync(client, jobId, baseApp).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{baseApp.ErrorsText}");
        Assert.Equal(100, job.GetProperty("percent").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Справжнє_сховище_звітує_кроки_у_порядку_і_закінчує_перемиканням_версії()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var recorded = new List<(string Stage, int Done, int Steps, long Rows)>();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVersionMigrationStore>(sp => new ObservingMigrationStore(
                new DocumentVersionMigrationStore(sp.GetRequiredService<EcrDbContext>()),
                (inner, projectId, targetVersionId, plan, onStep, ct) =>
                    inner.ApplyAsync(
                        projectId, targetVersionId, plan,
                        (stage, done, steps, rows, stepCt) =>
                        {
                            lock (recorded)
                            {
                                recorded.Add((stage, done, steps, rows));
                            }

                            return onStep is null ? Task.CompletedTask : onStep(stage, done, steps, rows, stepCt);
                        },
                        ct)))));

        using var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var started = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, started.StatusCode);
        var jobId = JsonDocument.Parse(await started.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("jobId").GetString()!;
        var job = await WaitJobAsync(client, jobId, baseApp).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{baseApp.ErrorsText}");

        (string Stage, int Done, int Steps, long Rows)[] steps;
        lock (recorded)
        {
            steps = [.. recorded];
        }

        // Стадії йдуть у порядку SQL, кроки не повертаються назад, останній — перемикання версії.
        Assert.NotEmpty(steps);
        Assert.Single(steps.Select(x => x.Steps).Distinct());
        for (var i = 1; i < steps.Length; i++)
        {
            Assert.True(steps[i].Done >= steps[i - 1].Done, $"крок повернувся назад: {steps[i - 1]} -> {steps[i]}");
        }

        var stages = steps.Select(x => x.Stage).Distinct().ToList();
        Assert.Equal(
            [
                VersionMigrationStages.Cells, VersionMigrationStages.Instances, VersionMigrationStages.NewRows,
                VersionMigrationStages.Header, VersionMigrationStages.Index, VersionMigrationStages.Workflow,
                VersionMigrationStages.Validation, VersionMigrationStages.Finish,
            ],
            stages);
        Assert.Equal(VersionMigrationStages.Finish, steps[^1].Stage);
        Assert.Equal(steps[^1].Steps, steps[^1].Done);
    }

    /// <summary>Справжнє сховище, чий <c>ApplyAsync</c> з приймачем кроків підміняє тест.</summary>
    private sealed class ObservingMigrationStore(
        IDocumentVersionMigrationStore inner,
        Func<IDocumentVersionMigrationStore, int, int, VersionMigrationPlan, VersionMigrationStepReporter?, CancellationToken, Task> apply)
        : IDocumentVersionMigrationStore
    {
        public Task<int?> LockProjectVersionAsync(int projectId, CancellationToken ct)
            => inner.LockProjectVersionAsync(projectId, ct);

        public Task<IReadOnlyList<long>> ListDocumentIdsAsync(int projectId, CancellationToken ct)
            => inner.ListDocumentIdsAsync(projectId, ct);

        public Task<VersionMigrationScope> ReadScopeAsync(int projectId, CancellationToken ct)
            => inner.ReadScopeAsync(projectId, ct);

        public Task<int> CountDenyGrantsAsync(
            IReadOnlyCollection<int> sheetIds, IReadOnlyCollection<int> tableIds, IReadOnlyCollection<int> columnIds,
            CancellationToken ct)
            => inner.CountDenyGrantsAsync(sheetIds, tableIds, columnIds, ct);

        public Task<int> CountUnmappedBindingsAsync(
            int sourceVersionId, IReadOnlyDictionary<int, int> columnMap, CancellationToken ct)
            => inner.CountUnmappedBindingsAsync(sourceVersionId, columnMap, ct);

        public Task<IReadOnlyList<string>> ListForeignMethodologyKeysAsync(int targetVersionId, int limit, CancellationToken ct)
            => inner.ListForeignMethodologyKeysAsync(targetVersionId, limit, ct);

        public Task<GrantedUsers> ListUsersWithGrantsAsync(VersionMigrationPlan plan, int limit, CancellationToken ct)
            => inner.ListUsersWithGrantsAsync(plan, limit, ct);

        public Task ApplyAsync(int projectId, int targetVersionId, VersionMigrationPlan plan, CancellationToken ct)
            => apply(inner, projectId, targetVersionId, plan, null, ct);

        public Task ApplyAsync(
            int projectId, int targetVersionId, VersionMigrationPlan plan, VersionMigrationStepReporter? onStep,
            CancellationToken ct)
            => apply(inner, projectId, targetVersionId, plan, onStep, ct);
    }
}

internal static class MigrationJobHttpExtensions
{
    /// <summary>Одне читання <c>GET /jobs/{jobId}</c>.</summary>
    public static async Task<JsonElement> GetFromJsonAsyncJob(this HttpClient client, string jobId)
    {
        var response = await client.GetAsync(
            new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative)).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"GET /jobs: {response.StatusCode}: {text}");

        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
