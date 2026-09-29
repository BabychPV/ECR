// tests/Ecr.Api.Tests/Security/SimulationPipelineApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// «View as» (<c>Security.Simulate</c>, ФВ-6.16a, D-96) наскрізно: профіль
/// суб'єкта справді підставляється в КОЖЕН наступний запит тієї самої cookie,
/// запис відхиляється, завершення повертає права актора.
/// </summary>
/// <remarks>
/// Аудит, раунд 2, §0.7 стверджував, що профіль симуляції будується, але в
/// запити не потрапляє. Тест іде справжнім входом, справжнім SQL і HTTP, а
/// актор і суб'єкт мають РІЗНІ гранти (кожен — лише на свій проєкт), тож
/// «бачить те, що бачить суб'єкт» не можна сплутати з «бачить усе».
///
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>SimulationAwareAccessDecisionService.BuildProfileAsync</c>
/// одразу делегувати в <c>inner</c> — червоніє крок (а) (актор бачить свій
/// проєкт і не бачить проєкту суб'єкта) і <c>cellPermissions</c> без
/// <c>SimulationReadOnly</c>; прибрати <c>SimulationReadOnlyMiddleware</c> —
/// червоніє крок (б).
/// </remarks>
[Collection("SqlServer")]
public sealed class SimulationPipelineApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Simulation-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.16a")]
    public async Task Запит_під_симуляцією_бачить_права_цілі_запис_відхилено_завершення_повертає_права_актора()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var period = s.SubjectDoc.PeriodKey.Value;

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.ActorName).ConfigureAwait(true);

        // ── 0. До симуляції — права актора ─────────────────────────────────
        await AssertSeesAsync(app, client, s, actorView: true, "до симуляції").ConfigureAwait(true);

        // ── Старт симуляції ────────────────────────────────────────────────
        var start = await client.PostAsJsonAsync(
            new Uri("/api/v1/security/simulation", UriKind.Relative),
            new { subjectUserId = s.SubjectId, reason = "перевірка конвеєра View as" }).ConfigureAwait(true);
        var startBody = await start.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(start.StatusCode == HttpStatusCode.Created, $"старт: {start.StatusCode}: {startBody}\n{app.ErrorsText}");
        var sessionId = JsonDocument.Parse(startBody).RootElement.GetProperty("sessionId").GetInt64();

        // ── (а) Читання — права СУБ'ЄКТА ───────────────────────────────────
        var me = await GetJsonAsync(app, client, "/api/v1/me").ConfigureAwait(true);
        Assert.True(me.GetProperty("isSimulation").GetBoolean(), me.GetRawText());
        Assert.Equal(s.SubjectId, me.GetProperty("simulatedForUserId").GetInt32());
        Assert.Equal(sessionId, me.GetProperty("simulationSessionId").GetInt64());

        await AssertSeesAsync(app, client, s, actorView: false, "під симуляцією").ConfigureAwait(true);

        // Правила комірок рахуються з профілем симуляції: кожна комірка
        // зрізу — `SimulationReadOnly` (D-96), хоча суб'єкт має Write.
        var slice = await GetJsonAsync(
            app, client, $"/api/v1/documents/{s.SubjectDoc.DocumentId}/tables/{s.SubjectDoc.TableInstanceId}").ConfigureAwait(true);
        var reasons = slice.GetProperty("cellPermissions").EnumerateObject().Select(p => p.Value.GetString()).ToList();
        Assert.NotEmpty(reasons);
        Assert.All(reasons, r => Assert.Equal(nameof(EditDenyReason.SimulationReadOnly), r));

        // ── (б) Запис — відмова ECR-SIM-0403 ───────────────────────────────
        var patch = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.SubjectDoc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId = s.SubjectDoc.TableInstanceId,
                periodKey = period,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = $"SIM{Guid.NewGuid():N}"[..12],
                        baseVersion = (string?)null,
                        cells = new object[] { new { columnCode = "X", value = (object)1m } },
                    },
                },
            }).ConfigureAwait(true);
        var patchBody = await patch.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(patch.StatusCode == HttpStatusCode.Forbidden, $"PATCH: {patch.StatusCode}: {patchBody}\n{app.ErrorsText}");
        Assert.Equal("ECR-SIM-0403", JsonDocument.Parse(patchBody).RootElement.GetProperty("errorCode").GetString());

        // ── (в) Завершення — знову права актора ────────────────────────────
        using (var end = await client.DeleteAsync(new Uri("/api/v1/security/simulation", UriKind.Relative)).ConfigureAwait(true))
        {
            Assert.True(
                end.StatusCode == HttpStatusCode.NoContent,
                $"завершення: {end.StatusCode}: {await end.Content.ReadAsStringAsync().ConfigureAwait(true)}\n{app.ErrorsText}");
        }

        var after = await GetJsonAsync(app, client, "/api/v1/me").ConfigureAwait(true);
        Assert.False(after.GetProperty("isSimulation").GetBoolean(), after.GetRawText());

        await AssertSeesAsync(app, client, s, actorView: true, "після завершення").ConfigureAwait(true);
    }

    /// <summary>
    /// Перелік проєктів і картка документа: з правами актора видно лише
    /// проєкт актора, з правами суб'єкта — лише проєкт суб'єкта.
    /// </summary>
    private static async Task AssertSeesAsync(EcrApiFactory app, HttpClient client, Scenario s, bool actorView, string stage)
    {
        var projects = await GetJsonAsync(app, client, "/api/v1/projects?limit=200").ConfigureAwait(false);
        var ids = projects.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetInt32()).ToList();

        Assert.True(
            ids.Contains(s.ActorDoc.ProjectId) == actorView && ids.Contains(s.SubjectDoc.ProjectId) == !actorView,
            $"{stage}: проєкти [{string.Join(",", ids)}], актора {s.ActorDoc.ProjectId}, суб'єкта {s.SubjectDoc.ProjectId}");

        foreach (var (doc, visible) in new[] { (s.ActorDoc, actorView), (s.SubjectDoc, !actorView) })
        {
            using var response = await client.GetAsync(new Uri($"/api/v1/documents/{doc.DocumentId}", UriKind.Relative)).ConfigureAwait(false);
            var expected = visible ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            Assert.True(
                response.StatusCode == expected,
                $"{stage}: документ {doc.DocumentId} — {response.StatusCode}, очікували {expected}\n{app.ErrorsText}");
        }
    }

    private static async Task<JsonElement> GetJsonAsync(EcrApiFactory app, HttpClient client, string url)
    {
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {url}: {response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>
    /// Два документи в двох проєктах. Актор: <c>Document.View</c> +
    /// <c>Security.Simulate</c>, <c>Read</c> лише на свій проєкт. Суб'єкт:
    /// <c>Document.View</c>, <c>Write</c> лише на свій проєкт.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var actorDoc = await builder.BuildAsync().ConfigureAwait(false);
        var subjectDoc = await builder.BuildAsync().ConfigureAwait(false);
        Assert.NotEqual(actorDoc.ProjectId, subjectDoc.ProjectId);

        await using var db = builder.CreateContext();

        var actor = NewUser("sima");
        var subject = NewUser("sims");
        db.Users.AddRange(actor, subject);

        var actorRole = NewRole("SIM_A");
        var subjectRole = NewRole("SIM_S");
        db.Roles.AddRange(actorRole, subjectRole);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(actorRole.Id, "Document.View"));
        db.RolePermissions.Add(new RolePermission(actorRole.Id, "Security.Simulate"));
        db.ResourceGrants.Add(new ResourceGrant(actorRole.Id, ResourceKind.Project, actorDoc.ProjectId, GrantLevel.Read));
        db.RoleAssignments.Add(new RoleAssignment(actorRole.Id, actor.Id, null));

        db.RolePermissions.Add(new RolePermission(subjectRole.Id, "Document.View"));
        db.ResourceGrants.Add(new ResourceGrant(subjectRole.Id, ResourceKind.Project, subjectDoc.ProjectId, GrantLevel.Write));
        db.RoleAssignments.Add(new RoleAssignment(subjectRole.Id, subject.Id, null));

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(actorDoc, subjectDoc, actor.UserName, subject.Id);
    }

    private static User NewUser(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        return user;
    }

    private static Role NewRole(string prefix)
        => new(
            EcrCode.Create($"{prefix}_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = prefix }));

    private sealed record Scenario(TestDocument ActorDoc, TestDocument SubjectDoc, string ActorName, int SubjectId);
}
