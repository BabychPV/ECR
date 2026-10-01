// tests/Ecr.Api.Tests/Security/PeriodPolicySharedUpdateApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// S19 аудиту безпеки: спільна політика періодів змінюється лише тим, хто
/// керує КОЖНИМ її проєктом, і зміна лишає запис у журналі структури.
/// </summary>
/// <remarks>
/// ⛔ Доказ на HTTP і справжній базі: перелік проєктів політики рахує запит
/// <c>PeriodStore.ListProjectIdsUsingPolicyAsync</c>, журнал — сирий
/// <c>INSERT</c> <c>AuditWriter</c> у транзакції зміни.
///
/// ⚠ Політика — ВЛАСНА для тесту: типову (першу в базі) використовують усі
/// проєкти <c>TestDocumentBuilder</c>, і її зміна зсунула б межі чужих тестів.
/// </remarks>
[Collection("SqlServer")]
public sealed class PeriodPolicySharedUpdateApiTests(SqlServerFixture sql)
{
    private const string Password = "Policy-Share-Probe-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "S19")]
    public async Task S19_без_Manage_на_один_проєкт_політики_403_з_Manage_на_всі_200_і_аудит()
    {
        var chain = await new TestDocumentBuilder(sql.ConnectionString)
            .BuildAsync(ct: CancellationToken.None).ConfigureAwait(true);
        var (policyId, projectA, projectB) =
            await PolicyWithTwoProjectsAsync(chain.TemplateVersionId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        // Manage лише на A: зміна зсунула б межі й у B — відмова.
        using (var partial = await SignedInAsync(app, [projectA]).ConfigureAwait(true))
        {
            using var denied = await UpdateAsync(partial, policyId, grace: 20).ConfigureAwait(true);

            Assert.True(denied.StatusCode == HttpStatusCode.Forbidden, $"{denied.StatusCode}: {app.ErrorsText}");
            var problem = await JsonAsync(denied).ConfigureAwait(true);
            Assert.Equal("ECR-AUTH-0403", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-AUTH-0403.periodPolicyShared", problem.GetProperty("messageKey").GetString());

            // Нічого не змінено й нічого не записано.
            Assert.Equal(15, await GraceAsync(policyId).ConfigureAwait(true));
            Assert.Empty(await AuditAsync(policyId).ConfigureAwait(true));
        }

        // Manage на обидва — зміна проходить і пише журнал зі старими й новими зсувами.
        using var manager = await SignedInAsync(app, [projectA, projectB]).ConfigureAwait(true);
        using var ok = await UpdateAsync(manager, policyId, grace: 20).ConfigureAwait(true);

        Assert.True(ok.StatusCode == HttpStatusCode.OK, $"{ok.StatusCode}: {app.ErrorsText}");
        Assert.Equal(20, await GraceAsync(policyId).ConfigureAwait(true));

        var rows = await AuditAsync(policyId).ConfigureAwait(true);
        var row = Assert.Single(rows);
        using var oldJson = JsonDocument.Parse(row.OldJson);
        using var newJson = JsonDocument.Parse(row.NewJson);
        Assert.Equal(15, oldJson.RootElement.GetProperty("graceOffsetDays").GetInt32());
        Assert.Equal(20, newJson.RootElement.GetProperty("graceOffsetDays").GetInt32());
        Assert.Equal(
            [projectA, projectB],
            newJson.RootElement.GetProperty("projectIds").EnumerateArray().Select(e => e.GetInt32()).Order());
    }

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, int policyId, int grace)
        => client.PutAsJsonAsync(
            new Uri($"/api/v1/projects/period-policies/{policyId}", UriKind.Relative),
            new { openOffsetDays = 0, graceOffsetDays = grace, hardCloseOffsetDays = 45, yearGraceOffsetDays = 45 });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    /// <summary>Нова політика (0/15/45/45) і два проєкти на ній.</summary>
    private async Task<(int PolicyId, int ProjectA, int ProjectB)> PolicyWithTwoProjectsAsync(int templateVersionId)
    {
        await using var db = NewDb();
        var tag = $"{Guid.NewGuid():N}"[..8];

        var policy = new PeriodPolicy(EcrCode.Create($"POL{tag}"), 0, 15, 45, 45);
        db.PeriodPolicies.Add(policy);
        await db.SaveChangesAsync().ConfigureAwait(false);

        Project NewProject(string suffix) => new(
            EcrCode.Create($"S19{suffix}{tag}"), Name($"S19 {suffix}"),
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31),
            templateVersionId, PeriodKind.Monthly, policy.Id, "Asia/Atyrau");

        var a = NewProject("A");
        var b = NewProject("B");
        db.Projects.AddRange(a, b);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (policy.Id, a.Id, b.Id);
    }

    private async Task<int> GraceAsync(int policyId)
    {
        await using var db = NewDb();
        return await db.PeriodPolicies.AsNoTracking().Where(p => p.Id == policyId)
            .Select(p => p.GraceOffsetDays).SingleAsync().ConfigureAwait(false);
    }

    private sealed record AuditRow(string OldJson, string NewJson);

    private async Task<List<AuditRow>> AuditAsync(int policyId)
    {
        await using var db = NewDb();
        var raw = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(OldJson, N'') + N'|' + ISNULL(NewJson, N'') AS Value FROM aud.StructureChange WHERE EntityType = N'PeriodPolicy' AND EntityId = {policyId}")
            .ToListAsync().ConfigureAwait(false);

        return [.. raw.Select(r => r.Split('|', 2)).Select(p => new AuditRow(p[0], p[1]))];
    }

    /// <summary>Користувач з <c>Project.Manage</c> і грантом Manage на задані проєкти.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, IReadOnlyList<int> manageOn)
    {
        var name = $"s19_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("S19 policy probe"));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Project.Manage"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            foreach (var projectId in manageOn)
            {
                db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Manage));
            }

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password })
            .ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext NewDb()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
