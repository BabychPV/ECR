// tests/Ecr.Api.Tests/Security/DocumentListSummarySheetsScopeTests.cs
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
/// UI-18 (LS-B): <c>sheetsApproved</c>/<c>sheetsTotal</c> у зведенні переліку й <c>ownerDisplayName</c> у
/// документі рахуються ЛИШЕ по тому, що читач бачить (клас оракула R-1: різниця двох чисел
/// розкривала б кількість схованих аркушів).
/// </summary>
/// <remarks>
/// Документ із двома затвердженими аркушами: A видимий, B схований (звуження ролі аркушами D-214 або
/// заборона <c>Deny</c>). Звичайна роль — 2 із 2 і ім'я автора; звужена — 1 із 1 і <c>null</c> замість імені.
/// ⛔ Мутаційний доказ: прибрати <c>NOT EXISTS (hidden)</c> у <c>DocumentListSummaryStore</c> — звужена
/// роль отримує 2/2 і тести червоніють; прибрати обнулення в <c>DocumentSheetVisibility.For</c> — червоніє ім'я.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentListSummarySheetsScopeTests(SqlServerFixture sql)
{
    private const string Password = "Summary-Sheets-Scope-B!";

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Зведення_рахує_аркуші_лише_видимі_читачу(string how)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var body = await client.GetStringAsync(
            new Uri($"/api/v1/documents/summary?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true);
        var root = JsonDocument.Parse(body).RootElement;

        Assert.True(
            root.GetProperty("sheetsTotal").GetInt32() == 1 && root.GetProperty("sheetsApproved").GetInt32() == 1,
            $"ВИТІК: sheetsTotal/sheetsApproved мають бути 1/1 (лише видимий аркуш A), а не 2/2\n{body}");
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Звужений_читач_не_отримує_імені_автора_ні_в_переліку_ні_в_документі(string how)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var list = await client.GetStringAsync(
            new Uri($"/api/v1/documents?limit=200&periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true);
        var item = JsonDocument.Parse(list).RootElement.GetProperty("items").EnumerateArray()
            .Single(d => d.GetProperty("id").GetInt64() == s.DocumentId);
        var one = await client.GetStringAsync(
            new Uri($"/api/v1/documents/{s.DocumentId}?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true);

        AssertOwnerHidden("GET /documents", item);
        AssertOwnerHidden("GET /documents/{id}", JsonDocument.Parse(one).RootElement);
    }

    /// <summary>Звичайна роль: ім'я автора є, 2 із 2, і зведення збігається з сумою по переліку.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Звичайна_роль_бачить_ім_я_автора_і_зведення_дорівнює_сумі_по_переліку()
    {
        var s = await ArrangeAsync("none").ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var list = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/v1/documents?limit=200&periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true)).RootElement;
        var items = list.GetProperty("items").EnumerateArray().ToList();
        var summary = JsonDocument.Parse(await client.GetStringAsync(
            new Uri($"/api/v1/documents/summary?periodKey={s.PeriodKey}", UriKind.Relative)).ConfigureAwait(true)).RootElement;

        Assert.Equal(items.Sum(d => d.GetProperty("sheetCount").GetInt32()), summary.GetProperty("sheetsTotal").GetInt32());
        Assert.Equal(
            items.Sum(d => d.GetProperty("sheets").EnumerateArray().Count(x => x.GetProperty("state").GetString() == "Approved")),
            summary.GetProperty("sheetsApproved").GetInt32());
        Assert.Equal(2, summary.GetProperty("sheetsTotal").GetInt32());
        Assert.Equal(2, summary.GetProperty("sheetsApproved").GetInt32());

        var mine = items.Single(d => d.GetProperty("id").GetInt64() == s.DocumentId);
        Assert.Equal(s.UserName, mine.GetProperty("ownerDisplayName").GetString());
    }

    private static void AssertOwnerHidden(string endpoint, JsonElement doc)
    {
        var shown = doc.TryGetProperty("ownerDisplayName", out var v) && v.ValueKind != JsonValueKind.Null;
        Assert.False(shown, $"{endpoint}: ownerDisplayName звуженому читачу — {doc}");
    }

    private sealed record Scenario(long DocumentId, int PeriodKey, string UserName);

    private async Task<Scenario> ArrangeAsync(string how)
    {
        var b = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync().ConfigureAwait(false);
        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var now = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var userName = $"sum{Guid.NewGuid():N}"[..20];

        await using var db = Context();

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));
        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create($"SUMSH{tag}"), Name($"SumSheet{tag}"), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheetB.Id));

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("LS-B"));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, b.ProjectId, GrantLevel.Read));
        if (how == "deny")
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Sheet, sheetB.Id, GrantLevel.Read, isDeny: true));
        }

        var assignment = new RoleAssignment(role.Id, user.Id, principalSid: null);
        db.RoleAssignments.Add(assignment);
        await db.SaveChangesAsync().ConfigureAwait(false);

        if (how == "scope")
        {
            var scope = RoleAssignmentScope.Create([b.ProjectId], [b.SheetCode], null, null).ToJson();
            await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE sec.RoleAssignment SET ScopeJson = {scope} WHERE Id = {assignment.Id}")
                .ConfigureAwait(false);
        }

        // Автор документа — користувач сценарію: його ім'я бачить звичайна роль і не бачить звужена.
        await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE doc.Document SET CreatedByUserId = {user.Id} WHERE Id = {b.DocumentId}")
            .ConfigureAwait(false);

        // Обидва аркуші затверджено.
        foreach (var sheetId in new[] { b.SheetDefId, sheetB.Id })
        {
            var state = new ApprovalState(b.DocumentId, sheetId, b.PeriodKey.Value);
            state.Submit(user.Id, now);
            state.Approve(user.Id, now);
            db.ApprovalStates.Add(state);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(b.DocumentId, b.PeriodKey.Value, userName);
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
