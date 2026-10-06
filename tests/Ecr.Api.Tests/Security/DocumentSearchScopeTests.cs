// tests/Ecr.Api.Tests/Security/DocumentSearchScopeTests.cs
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// UI-18 (LS-A): пошук <c>q</c> у <c>GET /documents</c> шукає лише по полях самого документа,
/// у межах його видимості, і не підтверджує існування прихованого аркуша чи чужого документа.
/// </summary>
/// <remarks>
/// Роль звужено двома способами (як у D-214 і ФВ-6.6): призначення з переліком аркушів
/// (<c>scope</c>) і ресурсна заборона на аркуш (<c>deny</c>). Назва й код прихованого аркуша B
/// — унікальні рядки; чужий проєкт має документ із кодом, що збігається з запитом.
/// ⛔ Мутаційний доказ: розширити пошук на назви аркушів або прибрати межу проєктів — тест червоніє
/// на відповідному запиті.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentSearchScopeTests(SqlServerFixture sql)
{
    private const string Password = "Doc-Search-Scope-2026!";

    [Theory]
    [InlineData("scope")]
    [InlineData("deny")]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.14")]
    public async Task Пошук_не_знаходить_за_прихованим_аркушем_і_чужим_проєктом(string how)
    {
        var s = await ArrangeAsync(how).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        // Назва й код прихованого аркуша не є полем документа — збігу немає.
        foreach (var q in new[] { s.HiddenName, s.HiddenCode, s.HiddenName.ToUpperInvariant() })
        {
            var (ids, body) = await SearchAsync(client, s, q).ConfigureAwait(true);
            Assert.True(ids.Count == 0, $"q={q} знайшов документ через прихований аркуш: {body}");
            Assert.DoesNotContain(s.HiddenName, body, StringComparison.Ordinal);
            Assert.DoesNotContain(s.HiddenCode, body, StringComparison.Ordinal);
        }

        // Документ чужого проєкту без гранта не з'являється, хоч його код збігається.
        var (foreignIds, foreignBody) = await SearchAsync(client, s, s.ForeignKey).ConfigureAwait(true);
        Assert.True(foreignIds.Count == 0, $"q знайшов документ чужого проєкту: {foreignBody}");
        Assert.DoesNotContain(s.ForeignKey, foreignBody, StringComparison.Ordinal);

        // Видимий документ, як і без пошуку, знаходиться за власним кодом.
        var (own, ownBody) = await SearchAsync(client, s, s.DocumentKey.ToLowerInvariant()).ConfigureAwait(true);
        Assert.True(own.SequenceEqual([s.DocumentId]), $"власний документ не знайдено: {ownBody}");

        // Спільний префікс ловить і видимий, і чужий: чужий не повертається.
        var (shared, sharedBody) = await SearchAsync(client, s, s.SharedPrefix).ConfigureAwait(true);
        Assert.True(shared.SequenceEqual([s.DocumentId]), $"спільний префікс розкрив зайве: {sharedBody}");
    }

    private static async Task<(List<long> Ids, string Body)> SearchAsync(HttpClient client, Scenario s, string q)
    {
        var body = await client.GetStringAsync(
            new Uri($"/api/v1/documents?limit=200&periodKey={s.PeriodKey}&q={Uri.EscapeDataString(q)}", UriKind.Relative))
            .ConfigureAwait(false);

        var ids = JsonDocument.Parse(body).RootElement.GetProperty("items").EnumerateArray()
            .Select(d => d.GetProperty("id").GetInt64()).ToList();

        return (ids, body);
    }

    private sealed record Scenario(
        long DocumentId, int PeriodKey, string UserName, string HiddenCode, string HiddenName,
        string DocumentKey, string ForeignKey, string SharedPrefix);

    private async Task<Scenario> ArrangeAsync(string how)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var b = await builder.BuildAsync().ConfigureAwait(false);
        var foreign = await builder.BuildAsync().ConfigureAwait(false);

        var tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var hiddenCode = $"HIDSRCH{tag}";
        var hiddenName = $"HiddenSearchName{tag}";
        var sharedPrefix = $"SRCH{tag}";
        var documentKey = $"{sharedPrefix}-OWN";
        var foreignKey = $"{sharedPrefix}-FOREIGN";
        var userName = $"srch{Guid.NewGuid():N}"[..20];

        await using var db = Context();

        // Код видимого документа й чужого — зі спільним префіксом.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET BusinessKey = {documentKey} WHERE Id = {b.DocumentId}").ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE doc.Document SET BusinessKey = {foreignKey} WHERE Id = {foreign.DocumentId}").ConfigureAwait(false);

        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, b.SheetDefId));

        var sheetB = new SheetDef(b.TemplateVersionId, EcrCode.Create(hiddenCode), Name(hiddenName), 2);
        db.SheetDefs.Add(sheetB);
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.DocumentSheets.Add(new DocumentSheet(b.DocumentId, sheetB.Id));

        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);
        var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Search narrowed"));
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

        return new Scenario(
            b.DocumentId, b.PeriodKey.Value, userName, hiddenCode, hiddenName, documentKey, foreignKey, sharedPrefix);
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
