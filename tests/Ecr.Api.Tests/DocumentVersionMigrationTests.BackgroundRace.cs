// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.BackgroundRace.cs
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Infrastructure.Jobs;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-2 (правки «Аудиту»): повторна перевірка прав у мить виконання задачі і гонка двох постановок.
/// </summary>
/// <remarks>
/// ⛔ Задача стоїть ДО початку роботи (<see cref="GatedMigrationJob"/>), поки тест міняє права або ставить
/// другу постановку: так порядок подій детермінований, без пауз за часом.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Грант_Manage_знижено_до_старту_задачі_задача_Failed_403_і_документи_на_старій_версії()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);
        var gate = new ApplyPause();

        using var baseApp = new EcrApiFactory(sql);
        using var app = GatedApp(baseApp, gate);
        using var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var jobId = await StartAsync(client, s).ConfigureAwait(true);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);

        // Між постановкою і стартом грант Manage знижено до Write.
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var roleId = await db.RoleAssignments.AsNoTracking()
                .Where(a => a.UserId == s.UserId).Select(a => a.RoleId).SingleAsync().ConfigureAwait(true);
            await db.ResourceGrants
                .Where(g => g.RoleId == roleId && g.ResourceKind == ResourceKind.Project && g.ResourceId == s.Doc.ProjectId)
                .ExecuteDeleteAsync().ConfigureAwait(true);
            db.ResourceGrants.Add(new ResourceGrant(roleId, ResourceKind.Project, s.Doc.ProjectId, GrantLevel.Write));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        InvalidateProfiles(app);
        gate.Release.TrySetResult();

        var job = await WaitJobAsync(client, jobId, baseApp).ConfigureAwait(true);

        // ⛔ Предмет тесту: задача не пише від імені людини, яка вже не має права.
        Assert.Equal("Failed", job.GetProperty("state").GetString());
        Assert.Equal("ECR-AUTH-0403", job.GetProperty("errorCode").GetString());
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Право_Template_Edit_відкликано_до_старту_задачі_задача_Failed_403_і_документи_на_старій_версії()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);
        var gate = new ApplyPause();

        using var baseApp = new EcrApiFactory(sql);
        using var app = GatedApp(baseApp, gate);
        using var client = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var jobId = await StartAsync(client, s).ConfigureAwait(true);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);

        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var roleId = await db.RoleAssignments.AsNoTracking()
                .Where(a => a.UserId == s.UserId).Select(a => a.RoleId).SingleAsync().ConfigureAwait(true);
            await db.RolePermissions
                .Where(p => p.RoleId == roleId && p.PermissionCode == MigrateDocumentVersionHandler.Permission)
                .ExecuteDeleteAsync().ConfigureAwait(true);
        }

        InvalidateProfiles(app);
        gate.Release.TrySetResult();

        var job = await WaitJobAsync(client, jobId, baseApp).ConfigureAwait(true);

        Assert.Equal("Failed", job.GetProperty("state").GetString());
        Assert.Equal("ECR-AUTH-0403", job.GetProperty("errorCode").GetString());
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Гонка_двох_людей_одна_отримує_202_друга_409_без_чужого_jobId()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var other = await AddSecondManagerAsync(s).ConfigureAwait(true);
        var gate = new ApplyPause();
        var rendezvous = new Rendezvous(2);

        using var baseApp = new EcrApiFactory(sql);
        using var app = GatedApp(baseApp, gate, rendezvous);
        using var first = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);
        using var second = await SignedInAsync(app, other, baseApp).ConfigureAwait(true);

        // ⛔ Бар'єр стоїть після пошуку активної задачі: обидва запити не бачать нічого й обидва йдуть
        // у постановку - саме вікно, де раніше друга людина діставала чужий jobId.
        var responses = await Task.WhenAll(PostAsyncJob(first, s, "Safe"), PostAsyncJob(second, s, "Safe")).ConfigureAwait(true);
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync())).ConfigureAwait(true);

        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Accepted);
        var conflicts = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.True(accepted == 1 && conflicts == 1, string.Join(" | ", responses.Select((r, i) => $"{r.StatusCode}: {bodies[i]}")));

        var winner = Array.FindIndex(responses, r => r.StatusCode == HttpStatusCode.Accepted);
        var loser = 1 - winner;
        var jobId = JsonDocument.Parse(bodies[winner]).RootElement.GetProperty("jobId").GetString()!;
        var problem = JsonDocument.Parse(bodies[loser]).RootElement;
        Assert.Equal("ECR-JOB-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-JOB-0409.migrationInProgress", problem.GetProperty("messageKey").GetString());
        Assert.DoesNotContain(jobId, bodies[loser], StringComparison.Ordinal);

        gate.Release.TrySetResult();
        var job = await WaitJobAsync(winner == 0 ? first : second, jobId, baseApp).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{baseApp.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Гонка_того_самого_автора_обидва_запити_отримують_один_jobId_і_переносу_один()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var gate = new ApplyPause();
        var rendezvous = new Rendezvous(2);

        using var baseApp = new EcrApiFactory(sql);
        using var app = GatedApp(baseApp, gate, rendezvous);
        using var a = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);
        using var b = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var responses = await Task.WhenAll(PostAsyncJob(a, s, "Safe"), PostAsyncJob(b, s, "Safe")).ConfigureAwait(true);
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync())).ConfigureAwait(true);

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var ids = bodies.Select(x => JsonDocument.Parse(x).RootElement.GetProperty("jobId").GetString()!).ToList();
        Assert.Single(ids.Distinct());

        gate.Release.TrySetResult();
        var job = await WaitJobAsync(a, ids[0], baseApp).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{baseApp.ErrorsText}");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var events = await db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentVersionMigrated' AND ChangedByUserId = {s.UserId}")
            .SingleAsync().ConfigureAwait(true);
        Assert.Equal(1, events);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Статус_задачі_переносу_чужій_людині_не_віддається_через_HTTP()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var other = await AddSecondManagerAsync(s).ConfigureAwait(true);
        var gate = new ApplyPause();

        using var baseApp = new EcrApiFactory(sql);
        using var app = GatedApp(baseApp, gate);
        using var owner = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);
        using var stranger = await SignedInAsync(app, other, baseApp).ConfigureAwait(true);

        var jobId = await StartAsync(owner, s).ConfigureAwait(true);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);

        // Автор свою задачу бачить; чужа людина (навіть з тим самим грантом Manage) - ні, і відповідь
        // така сама, як на неіснуючу задачу: наявність задачі не розкривається.
        var own = await owner.GetAsync(new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative)).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        var foreign = await stranger.GetAsync(new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative)).ConfigureAwait(true);
        Assert.True(
            foreign.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden,
            $"чужа людина отримала {foreign.StatusCode}");
        Assert.DoesNotContain("percent", await foreign.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);

        gate.Release.TrySetResult();
        _ = await WaitJobAsync(owner, jobId, baseApp).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Пошук_активних_сліпий_коалесценція_віддала_чужий_jobId_409_а_не_витік()
    {
        // Друга лінія захисту (інший процес): перелік активних задач порожній, а постановка на ціль проєкту
        // повертає jobId першої людини - його не можна віддавати другій.
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var other = await AddSecondManagerAsync(s).ConfigureAwait(true);
        var gate = new ApplyPause();

        using var baseApp = new EcrApiFactory(sql);
        using var app = GatedApp(baseApp, gate, blind: true);
        using var owner = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);
        using var stranger = await SignedInAsync(app, other, baseApp).ConfigureAwait(true);

        var jobId = await StartAsync(owner, s).ConfigureAwait(true);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);

        var refused = await PostAsyncJob(stranger, s, "Safe").ConfigureAwait(true);
        var body = await AssertProblemAsync(
            refused, HttpStatusCode.Conflict, "ECR-JOB-0409", "err.ECR-JOB-0409.migrationInProgress").ConfigureAwait(true);
        Assert.DoesNotContain(jobId, body, StringComparison.Ordinal);

        gate.Release.TrySetResult();
        _ = await WaitJobAsync(owner, jobId, baseApp).ConfigureAwait(true);
    }

    private async Task<string> StartAsync(HttpClient client, Scenario s)
    {
        var response = await PostAsyncJob(client, s, "Safe").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {body}");

        return JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!;
    }

    private static void InvalidateProfiles(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IAccessProfileInvalidator>().InvalidateAll();
    }

    /// <summary>Застосунок, де задача переносу стоїть до сигналу тесту, а за потреби - постановки йдуть через бар'єр.</summary>
    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> GatedApp(
        EcrApiFactory baseApp, ApplyPause gate, Rendezvous? rendezvous = null, bool blind = false)
        => baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddScoped<IMigrateDocumentVersionJob>(sp => new GatedMigrationJob(
                ActivatorUtilities.CreateInstance<MigrateDocumentVersionJob>(sp), gate));

            if (rendezvous is null && !blind)
            {
                return;
            }

            var original = services.Last(d => d.ServiceType == typeof(IBackgroundJobScheduler));
            services.Remove(original);
            services.AddScoped<IBackgroundJobScheduler>(sp =>
            {
                var target = original.ImplementationFactory is { } factory
                    ? (IBackgroundJobScheduler)factory(sp)
                    : (IBackgroundJobScheduler)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!);
                var proxy = DispatchProxy.Create<IBackgroundJobScheduler, SchedulerBarrierProxy>();
                var barrier = (SchedulerBarrierProxy)(object)proxy;
                barrier.Target = target;
                barrier.Rendezvous = rendezvous;
                barrier.Blind = blind;

                return proxy;
            });
        }));

    /// <summary>Задача переносу, що не починає роботу, доки тест не відпустить.</summary>
    private sealed class GatedMigrationJob(IMigrateDocumentVersionJob inner, ApplyPause gate) : IMigrateDocumentVersionJob
    {
        public async Task ExecuteAsync(object? payload, IJobProgress progress, CancellationToken ct)
        {
            gate.Reached.TrySetResult();
            await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
            await inner.ExecuteAsync(payload, progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Зустріч двох учасників: обидва чекають, доки не прийде останній.
    /// </summary>
    /// <remarks>
    /// ⚠ М'яка: якщо другий не прийшов за 3 с (він стоїть на замку проєкту, поки перший чекає тут), перший іде
    /// далі. Без замка обидва зустрічаються миттєво - вікно "пошук → постановка" відкрите детерміновано, без пауз
    /// за часом; із замком другий не може туди потрапити, і тест перевіряє саме це.
    /// </remarks>
    public sealed class Rendezvous(int parties)
    {
        private readonly TaskCompletionSource all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrived;

        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref arrived) >= parties)
            {
                all.TrySetResult();
            }

            try
            {
                await all.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Другий учасник заблокований замком проєкту: йдемо далі.
            }
        }
    }

    /// <summary>
    /// Планувальник із бар'єром: після пошуку НЕЗАВЕРШЕНИХ (<c>Queued</c>) задач повертає результат лише тоді,
    /// коли до цієї точки дійшли обидва запити.
    /// </summary>
    public class SchedulerBarrierProxy : DispatchProxy
    {
        public IBackgroundJobScheduler Target { get; set; } = null!;

        public Rendezvous? Rendezvous { get; set; }

        /// <summary>"Сліпий" перелік активних задач: завжди порожній.</summary>
        public bool Blind { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);

            if (Blind && targetMethod.Name == nameof(IBackgroundJobScheduler.ListRecentAsync))
            {
                return Task.FromResult<IReadOnlyList<JobSummary>>([]);
            }

            object? result;
            try
            {
                result = targetMethod.Invoke(Target, args);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }

            return Rendezvous is not null
                   && targetMethod.Name == nameof(IBackgroundJobScheduler.ListRecentAsync)
                   && args is [JobListFilter { State: "Queued" }, ..]
                   && result is Task<IReadOnlyList<JobSummary>> list
                ? AfterAsync(list)
                : result;
        }

        private async Task<IReadOnlyList<JobSummary>> AfterAsync(Task<IReadOnlyList<JobSummary>> list)
        {
            var found = await list.ConfigureAwait(false);
            await Rendezvous!.ArriveAsync().ConfigureAwait(false);

            return found;
        }
    }
}
