// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.Background.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Ports;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-2 (приймальна RC15B): перенос проєкту на нову версію шаблону — ФОНОВОЮ задачею (<c>async = true</c>).
/// </summary>
/// <remarks>
/// ⛔ Що було. Перенос Safe на 3 документах (2,06 млн значень) ішов синхронним HTTP-запитом ~28 хв: за
/// проксі й браузером це таймаут, а виконання лишалося неочевидним. Тепер apply з <c>async = true</c> — <c>202</c> із
/// <c>jobId</c>; сухий прогін лишається синхронним.
///
/// ⚠ Усе нижче — справжнім HTTP на справжньому SQL Server і справжній черзі задач.
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Async_apply_202_із_jobId_і_перенос_виконується_фоновою_задачею_від_імені_автора()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        // ⛔ Предмет тесту: apply НЕ тримає запит на весь перенос — 202 і ідентифікатор задачі.
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {body} {app.ErrorsText}");
        var accepted = JsonDocument.Parse(body).RootElement;
        var jobId = accepted.GetProperty("jobId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(jobId), body);
        Assert.Equal(s.Doc.DocumentId, accepted.GetProperty("documentId").GetInt64());

        var job = await WaitJobAsync(client, jobId!, app).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{app.ErrorsText}");
        Assert.Equal(100, job.GetProperty("percent").GetInt32());

        // Підсумок — у повідомленні задачі мовою читача: версія, документи, значення.
        var message = job.GetProperty("message").GetString();
        Assert.Contains($"2.0.0.{s.Tag}", message, StringComparison.Ordinal);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var project = await db.Projects.AsNoTracking().SingleAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(true);
        Assert.Equal(s.TargetVersionId, project.TemplateVersionId);

        var cells = await db.CellValues.AsNoTracking()
            .Where(c => c.PeriodKeyValue == s.Doc.PeriodKey.Value && s.Doc.RowIds.Contains(c.TableRowId))
            .ToListAsync().ConfigureAwait(true);
        Assert.Equal(2, cells.Count);
        Assert.All(cells, c => Assert.Equal(s.TargetTableDefId, c.TableDefId));

        // ⛔ Від імені автора: журнал безпеки підписано тим, хто запустив, а не «системою» чи порожнім.
        var events = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentVersionMigrated' AND ChangedByUserId = {s.UserId}")
            .ToListAsync().ConfigureAwait(true);
        Assert.Equal(s.TargetVersionId, JsonDocument.Parse(Assert.Single(events)).RootElement.GetProperty("toVersionId").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Повторний_POST_під_час_переносу_повертає_той_самий_jobId_і_другого_переносу_не_ставить()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var pause = new ApplyPause();

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVersionMigrationStore>(sp => new PausingMigrationStore(
                new DocumentVersionMigrationStore(sp.GetRequiredService<EcrDbContext>()), pause))));

        using var first = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);
        using var second = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);

        var started = await PostAsyncJob(first, s, "Safe").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var jobId = JsonDocument.Parse(await started.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("jobId").GetString()!;

        // Перенос стоїть усередині транзакції (перед `ApplyAsync`): задача `Running`.
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);

        // ⛔ Предмет тесту: тричі повторений запит (інша вкладка, подвійний клік) — той самий jobId.
        foreach (var client in new[] { first, second, first })
        {
            var again = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
            var againBody = await again.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(again.StatusCode == HttpStatusCode.Accepted, $"{again.StatusCode}: {againBody} {baseApp.ErrorsText}");
            Assert.Equal(jobId, JsonDocument.Parse(againBody).RootElement.GetProperty("jobId").GetString());
        }

        pause.Release.TrySetResult();
        var job = await WaitJobAsync(first, jobId, baseApp).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{baseApp.ErrorsText}");

        // Другого переносу не було: одна подія журналу, один перехід версії.
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
    public async Task Перенос_проєкту_вже_йде_за_вказівкою_іншої_людини_409_і_чужий_jobId_не_віддається()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var pause = new ApplyPause();
        var other = await AddSecondManagerAsync(s).ConfigureAwait(true);

        using var baseApp = new EcrApiFactory(sql);
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddScoped<IDocumentVersionMigrationStore>(sp => new PausingMigrationStore(
                new DocumentVersionMigrationStore(sp.GetRequiredService<EcrDbContext>()), pause))));

        using var owner = await SignedInAsync(app, s.UserName, baseApp).ConfigureAwait(true);
        using var stranger = await SignedInAsync(app, other, baseApp).ConfigureAwait(true);

        var started = await PostAsyncJob(owner, s, "Safe").ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var jobId = JsonDocument.Parse(await started.Content.ReadAsStringAsync().ConfigureAwait(true))
            .RootElement.GetProperty("jobId").GetString()!;
        await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(true);

        var refused = await PostAsyncJob(stranger, s, "Safe").ConfigureAwait(true);
        var body = await AssertProblemAsync(
            refused, HttpStatusCode.Conflict, "ECR-JOB-0409", "err.ECR-JOB-0409.migrationInProgress").ConfigureAwait(true);
        Assert.DoesNotContain(jobId, body, StringComparison.Ordinal);

        pause.Release.TrySetResult();
        var job = await WaitJobAsync(owner, jobId, baseApp).ConfigureAwait(true);
        Assert.True(job.GetProperty("state").GetString() == "Succeeded", $"задача: {job.GetRawText()}\n{baseApp.ErrorsText}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Async_невидимий_документ_404_а_без_права_і_без_Manage_403_і_нічого_не_змінюється()
    {
        // Невидимий: роль Template.Edit, але грантів на проєкт документа немає.
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var outsider = await AddOutsiderAsync().ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using (var client = await SignedInAsync(app, outsider).ConfigureAwait(true))
        {
            var response = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
            var notFound = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{response.StatusCode}: {notFound} {app.ErrorsText}");
        }

        // Без права Template.Edit.
        var noPermission = await ArrangeAsync(Target.DropsC3AddsC4AndRow, permission: "Document.View").ConfigureAwait(true);
        using (var client = await SignedInAsync(app, noPermission.UserName).ConfigureAwait(true))
        {
            var response = await PostAsyncJob(client, noPermission, "Safe").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        // Грант нижче Manage.
        var lowGrant = await ArrangeAsync(Target.DropsC3AddsC4AndRow, grant: GrantLevel.Write).ConfigureAwait(true);
        using (var client = await SignedInAsync(app, lowGrant.UserName).ConfigureAwait(true))
        {
            var response = await PostAsyncJob(client, lowGrant, "Safe").ConfigureAwait(true);
            await AssertProblemAsync(
                response, HttpStatusCode.Forbidden, "ECR-AUTH-0403", "err.ECR-AUTH-0403.noProjectManageGrant").ConfigureAwait(true);
        }

        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Async_архівований_проєкт_409_одразу_без_задачі()
    {
        var s = await ArrangeAsync(Target.OnlyLabels).ConfigureAwait(true);

        var now = new DateTime(2026, 1, 16, 9, 0, 0, DateTimeKind.Utc);
        await using (var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext())
        {
            var project = await db.Projects.Include(p => p.Periods)
                .FirstAsync(p => p.Id == s.Doc.ProjectId).ConfigureAwait(true);
            project.Activate(now);
            foreach (var period in project.Periods)
            {
                period.AdvanceTo(PeriodState.Closed, now);
            }

            project.Archive(now);
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "ECR-DOC-0409", "err.ECR-DOC-0409.migrateProjectArchived").ConfigureAwait(true);
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Async_відмова_за_даними_у_задачі_Failed_з_кодом_і_документи_лишаються_на_старій_версії()
    {
        // DropsC2: у колонці, яку нова версія прибирає, є значення — Safe відмовляє. План рахує САМА задача
        // (на мільйонах значень він іде десятки секунд), тож відповідь запиту — 202, а відмова — стан задачі.
        var s = await ArrangeAsync(Target.DropsC2).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsyncJob(client, s, "Safe").ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, $"{response.StatusCode}: {body} {app.ErrorsText}");
        var jobId = JsonDocument.Parse(body).RootElement.GetProperty("jobId").GetString()!;

        var job = await WaitJobAsync(client, jobId, app).ConfigureAwait(true);

        Assert.Equal("Failed", job.GetProperty("state").GetString());
        Assert.Equal("ECR-SCHM-0422", job.GetProperty("errorCode").GetString());
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Сухий_прогін_лишається_синхронним_навіть_з_async()
    {
        var s = await ArrangeAsync(Target.DropsC3AddsC4AndRow).ConfigureAwait(true);
        var before = await SnapshotAsync(s).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new { targetVersionId = s.TargetVersionId, mode = "Safe", dryRun = true, async = true }).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body} {app.ErrorsText}");
        var report = JsonDocument.Parse(body).RootElement;
        Assert.True(report.GetProperty("dryRun").GetBoolean());
        Assert.Equal(before, await SnapshotAsync(s).ConfigureAwait(true));
    }

    private static Task<HttpResponseMessage> PostAsyncJob(HttpClient client, Scenario s, string mode)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId.ToString(CultureInfo.InvariantCulture)}/migrate-version", UriKind.Relative),
            new { targetVersionId = s.TargetVersionId, mode, dryRun = false, async = true });

    /// <summary>Опитує <c>GET /jobs/{jobId}</c>, поки задача не завершиться (до хвилини).</summary>
    private static async Task<JsonElement> WaitJobAsync(HttpClient client, string jobId, EcrApiFactory app)
    {
        JsonElement job = default;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync(
                new Uri($"/api/v1/jobs/{Uri.EscapeDataString(jobId)}", UriKind.Relative)).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(response.IsSuccessStatusCode, $"GET /jobs: {response.StatusCode}: {text}\n{app.ErrorsText}");
            job = JsonDocument.Parse(text).RootElement.Clone();
            if (job.GetProperty("state").GetString() is not ("Queued" or "Running"))
            {
                return job;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        return job;
    }

    /// <summary>Користувач із <c>Template.Edit</c> без жодного гранта на проєкти — документ для нього невидимий.</summary>
    private async Task<string> AddOutsiderAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = builder.CreateContext();

        var userName = $"migo_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"MIGO_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Outsider" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, MigrateDocumentVersionHandler.Permission));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return userName;
    }

    /// <summary>Друга людина з тими самими правами й грантом Manage на проєкт сценарію.</summary>
    private async Task<string> AddSecondManagerAsync(Scenario s)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = builder.CreateContext();

        var roleId = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.UserId == s.UserId).Select(a => a.RoleId).SingleAsync().ConfigureAwait(false);

        var userName = $"migs_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RoleAssignments.Add(new RoleAssignment(roleId, user.Id, null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return userName;
    }
}
