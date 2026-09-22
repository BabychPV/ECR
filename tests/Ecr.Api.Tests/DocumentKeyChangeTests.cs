// tests/Ecr.Api.Tests/DocumentKeyChangeTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Documents;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary><c>POST /api/v1/documents/{id}/business-key</c> — ФВ-3.9, справжнім HTTP.</summary>
[Collection("SqlServer")]
public sealed class DocumentKeyChangeTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-Rekey-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-3.9")]
    public async Task Ключ_змінюється_і_в_журналі_старий_новий_і_причина()
    {
        var s = await ArrangeAsync(ChangeDocumentKeyHandler.Permission).ConfigureAwait(true);
        var newKey = $"NEW-{Guid.NewGuid():N}"[..16];

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s.DocumentId, newKey, s.OldKey, "Customer renumbered the file").ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {app.ErrorsText}");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var stored = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == s.DocumentId).ConfigureAwait(true);
        Assert.Equal(newKey, stored.BusinessKey);

        var events = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentKeyChanged' AND ChangedByUserId = {s.UserId}")
            .ToListAsync().ConfigureAwait(true);

        var details = JsonDocument.Parse(Assert.Single(events)).RootElement;
        Assert.Equal(s.DocumentId, details.GetProperty("documentId").GetInt64());
        Assert.Equal(s.OldKey, details.GetProperty("oldKey").GetString());
        Assert.Equal(newKey, details.GetProperty("newKey").GetString());
        Assert.Equal("Customer renumbered the file", details.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Ключ_іншого_документа_проєкту_дає_409()
    {
        var s = await ArrangeAsync(ChangeDocumentKeyHandler.Permission).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s.DocumentId, s.TakenKey, s.OldKey, "merge").ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "err.ECR-DOC-0409.rekeyDuplicate").ConfigureAwait(true);
        await AssertKeyUnchangedAsync(s).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застарілий_очікуваний_ключ_дає_409()
    {
        var s = await ArrangeAsync(ChangeDocumentKeyHandler.Permission).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s.DocumentId, "FRESH-1", "SOMEONE-ELSE", "typo").ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "err.ECR-DOC-0409.rekeyStale").ConfigureAwait(true);
        await AssertKeyUnchangedAsync(s).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поданий_аркуш_дає_409_rekeyLocked()
    {
        var s = await ArrangeAsync(ChangeDocumentKeyHandler.Permission, submitted: true).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s.DocumentId, "FRESH-2", s.OldKey, "typo").ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "err.ECR-DOC-0409.rekeyLocked").ConfigureAwait(true);
        await AssertKeyUnchangedAsync(s).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_причини_422()
    {
        var s = await ArrangeAsync(ChangeDocumentKeyHandler.Permission).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s.DocumentId, "FRESH-3", s.OldKey, "   ").ConfigureAwait(true);

        await AssertProblemAsync(response, HttpStatusCode.UnprocessableEntity, "err.ECR-DOC-0422.rekeyReasonRequired").ConfigureAwait(true);
        await AssertKeyUnchangedAsync(s).ConfigureAwait(true);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Document_ChangeKey_403()
    {
        var s = await ArrangeAsync("Document.View").ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await PostAsync(client, s.DocumentId, "FRESH-4", s.OldKey, "typo").ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertKeyUnchangedAsync(s).ConfigureAwait(true);
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, long documentId, string businessKey, string expected, string reason)
        => client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}/business-key", UriKind.Relative),
            new { businessKey, expectedBusinessKey = expected, reason });

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string messageKey)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {body}");
        Assert.Equal(messageKey, JsonDocument.Parse(body).RootElement.GetProperty("messageKey").GetString());
    }

    private async Task AssertKeyUnchangedAsync(Scenario s)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var stored = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == s.DocumentId).ConfigureAwait(false);
        Assert.Equal(s.OldKey, stored.BusinessKey);
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

    /// <summary>Документ, сусід у тому ж проєкті з зайнятим ключем, користувач із роллю й грантом Write.</summary>
    private async Task<Scenario> ArrangeAsync(string permission, bool submitted = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 1).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        if (submitted)
        {
            var state = new ApprovalState(document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            state.Submit(1, new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
            db.ApprovalStates.Add(state);
        }

        var userName = $"rekey_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"REKEY_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Doc rekey" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var takenKey = $"TAKEN-{Guid.NewGuid():N}"[..18];
        db.Documents.Add(new Document(document.ProjectId, takenKey, user.Id, DateTime.UtcNow));
        db.RolePermissions.Add(new RolePermission(role.Id, permission));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, GrantLevel.Write));
        await db.SaveChangesAsync().ConfigureAwait(false);

        var oldKey = await db.Documents.AsNoTracking()
            .Where(d => d.Id == document.DocumentId).Select(d => d.BusinessKey)
            .SingleAsync().ConfigureAwait(false);

        return new Scenario(document.DocumentId, oldKey, takenKey, userName, user.Id);
    }

    private sealed record Scenario(long DocumentId, string OldKey, string TakenKey, string UserName, int UserId);
}
