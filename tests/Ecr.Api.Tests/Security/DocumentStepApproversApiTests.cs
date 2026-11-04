// tests/Ecr.Api.Tests/Security/DocumentStepApproversApiTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// AN-59 (варіант C, D-466): <c>stepApproverNames</c> у картці документа — є у читача без обмежень, <c>null</c> у
/// звуженого (як <c>ownerDisplayName</c>, D-300), і ніколи не потрапляє в перелік документів.
/// </summary>
/// <remarks>
/// Документ із двома поданими аркушами: A видимий, B схований (звуження ролі D-214 або <c>Deny</c>). Крок маршруту —
/// роль з грантом Approve; її носій — «Погоджувач». ⛔ Мутаційний доказ: прибрати перевірку
/// <c>DocumentSheetVisibility.IsNarrowedFor</c> у <c>GetDocumentHandler</c> — звужений читач отримує імена, тести червоні.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentStepApproversApiTests(SqlServerFixture sql)
{
    private const string Password = "Step-Approvers-Names-B!";

    private sealed record Scenario(long DocumentId, int PeriodKey, string Reader, string ApproverName);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Читач_без_обмежень_бачить_імена_погоджувачів_а_перелік_поля_не_несе()
    {
        var s = await ArrangeAsync("none").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        var one = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/v1/documents/{s.DocumentId}?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        var list = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/v1/documents?limit=200&periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        var item = list.GetProperty("items").EnumerateArray().Single(d => d.GetProperty("id").GetInt64() == s.DocumentId);

        var names = one.GetProperty("stepApproverNames").EnumerateArray().Select(n => n.GetString()).ToList();
        Assert.Equal([s.ApproverName], names);
        Assert.False(one.GetProperty("stepApproverNamesTruncated").GetBoolean());
        Assert.False(item.TryGetProperty("stepApproverNames", out var inList) && inList.ValueKind != JsonValueKind.Null, item.ToString());
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "AN-59")]
    public async Task Звужений_читач_не_отримує_імен_погоджувачів_ні_кроків_схованого_аркуша(string how)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        var body = await client.GetStringAsync(
            new Uri($"/api/v1/documents/{s.DocumentId}?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true);
        var one = JsonDocument.Parse(body).RootElement;

        var shown = one.TryGetProperty("stepApproverNames", out var v) && v.ValueKind != JsonValueKind.Null;
        Assert.False(shown, $"ВИТІК: stepApproverNames звуженому читачеві — {body}");
        Assert.DoesNotContain(s.ApproverName, body, StringComparison.Ordinal);
    }

    private async Task<Scenario> ArrangeAsync(string how)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);
        await new TestDocumentBuilder(sql.ConnectionString).OpenPeriodAsync(b).ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var reader = $"sap{Guid.NewGuid():N}"[..20];
        var approverName = $"Approver {tag}";

        await using var db = Context();

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create($"SAPSH{tag}"), Name($"SapSheet{tag}"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheetB.Id));

        var user = new User(reader, reader, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        var approver = new User($"sapa{Guid.NewGuid():N}"[..20], approverName, AuthProvider.Local);
        approver.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.AddRange(user, approver);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("SAP-reader"));
        var stepRole = new Role(EcrCode.Create($"S{Guid.NewGuid():N}"[..12]), Name("SAP-step"));
        db.Roles.AddRange(role, stepRole);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Read));
        if (how == "deny")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, sheetB.Id, GrantLevel.Read, isDeny: true));
        }

        var assignment = new RoleAssignment(role.Id, user.Id, principalSid: null);
        db.RoleAssignments.Add(assignment);
        db.RoleAssignments.Add(new RoleAssignment(stepRole.Id, approver.Id, principalSid: null));
        db.ResourceGrants.Add(new ResourceGrant(stepRole.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Approve));
        var route = new ApprovalRoute(EcrCode.Create($"SAPR{Guid.NewGuid():N}"[..14]), Name("SAP route"), b.ProjectId);
        route.AddStep(stepRole.Id);
        db.ApprovalRoutes.Add(route);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (how == "scope")
        {
            var scope = RoleAssignmentScope.Create([b.ProjectId], [b.SheetCode], null, null).ToJson();
            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {assignment.Id}")
                .ConfigureAwait(false);
        }

        foreach (var sheetId in new[] { b.SheetDefId, sheetB.Id })
        {
            var state = new ApprovalState(b.DocumentId, sheetId, b.PeriodKey.Value);
            state.Submit(user.Id, now, route.Steps[0].Id);
            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(b.DocumentId, b.PeriodKey.Value, reader, approverName);
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

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
