// tests/Ecr.Api.Tests/CalculationRunStatusApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D14: <c>GET /documents/{id}/calculation-run</c> віддає з профілю актуального прогону рядки, яким не підійшло
/// жодне правило (напр. статус Removed), — лише цього документа, без профілю часу по модулях.
/// </summary>
/// <remarks>
/// Мутація: не писати <c>unmatched</c> у <c>ModuleProfile.ToJson</c> — червоніє перший тест.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationRunStatusApiTests(SqlServerFixture sql)
{
    private const string Password = "Run-Status-D14-Unmatched!";
    private static readonly DateTime RunAt = new(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Рядок_Removed_без_правила_віддається_з_ключем_і_без_чужих_рядків_та_профілю()
    {
        var s = await ArrangeAsync(withRun: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var reply = await GetAsync(client, s).ConfigureAwait(true);

        Assert.True(reply.Status == HttpStatusCode.OK, $"{reply.Status}: {reply.Body}\n{app.ErrorsText}");
        using var json = JsonDocument.Parse(reply.Body);
        var root = json.RootElement;
        Assert.True(root.GetProperty("runId").GetInt64() > 0);
        var unmatched = root.GetProperty("unmatchedRows");
        Assert.Equal(1, unmatched.GetProperty("count").GetInt32());
        var row = Assert.Single(unmatched.GetProperty("rows").EnumerateArray());
        Assert.Equal(s.RemovedRowKey, row.GetProperty("rowKey").GetString());
        Assert.Equal(2, row.GetProperty("rowNumber").GetInt32());
        Assert.DoesNotContain("OTHERDOC", reply.Body, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("modulesProfileJson", out _), reply.Body);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_прогону_відповідь_порожня_а_не_помилка()
    {
        var s = await ArrangeAsync(withRun: false).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var reply = await GetAsync(client, s).ConfigureAwait(true);

        Assert.True(reply.Status == HttpStatusCode.OK, $"{reply.Status}: {reply.Body}\n{app.ErrorsText}");
        using var json = JsonDocument.Parse(reply.Body);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("runId").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("unmatchedRows").ValueKind);
    }

    private sealed record Reply(HttpStatusCode Status, string Body);

    private sealed record Scenario(long DocumentId, int PeriodKey, string UserName, string RemovedRowKey);

    private static async Task<Reply> GetAsync(HttpClient client, Scenario s)
    {
        using var response = await client.GetAsync(
            new Uri(
                $"/api/v1/documents/{s.DocumentId.ToString(CultureInfo.InvariantCulture)}/calculation-run?periodKey={s.PeriodKey.ToString(CultureInfo.InvariantCulture)}",
                UriKind.Relative)).ConfigureAwait(false);

        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private async Task<Scenario> ArrangeAsync(bool withRun)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);
        var userName = $"crs{Guid.NewGuid():N}"[..20];
        var removedKey = $"RM{Guid.NewGuid():N}"[..10].ToUpperInvariant();

        await using var db = Context();

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(
            EcrCode.Create($"R{Guid.NewGuid():N}"[..12]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Run status role" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        foreach (var permission in new[] { "Document.View", "Calculation.View" })
        {
            db.RolePermissions.Add(new RolePermission(role.Id, permission));
        }

        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Read));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));

        if (withRun)
        {
            var profile = new ModuleProfile();
            profile.Record("M1", TimeSpan.FromMilliseconds(3), 2);
            profile.RecordUnmatched(
            [
                new UnmatchedRow(b.TableInstanceId, 2, removedKey, b.DocumentId),
                new UnmatchedRow(b.TableInstanceId + 1, 1, "OTHERDOC", b.DocumentId + 1),
            ]);

            var run = new CalculationRun(b.ProjectId, b.PeriodKey.Value, triggeredByUserId: null, RunAt);
            run.Complete("Succeeded", RunAt.AddMinutes(1), profile.ToJson(), errorMessage: null);
            run.MakeCurrent();
            db.CalculationRuns.Add(run);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(b.DocumentId, b.PeriodKey.Value, userName, removedKey);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
}