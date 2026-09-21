// tests/Ecr.Api.Tests/DocumentDeleteTests.cs
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

/// <summary>
/// <c>DELETE /api/v1/documents/{id}</c> — лише чернетка (рішення людини 2026-09-21).
/// </summary>
/// <remarks>
/// Наскрізно, справжнім HTTP: 409 із доменного винятку дає правило суфікса
/// <c>-0409</c> у <c>ExceptionHandlingMiddleware</c>, і модульний тест його не бачить.
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentDeleteTests(SqlServerFixture sql)
{
    private const string Password = "Api-Doc-Delete-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Чернетка_видаляється_разом_із_даними_і_лишає_слід_у_журналі_безпеки()
    {
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, GrantLevel.Write).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent, $"{response.StatusCode}: {app.ErrorsText}");

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var id = s.Document.DocumentId;

        Assert.False(await db.Documents.AnyAsync(d => d.Id == id).ConfigureAwait(true));
        Assert.False(await db.TableInstances.AnyAsync(i => i.DocumentId == id).ConfigureAwait(true));
        Assert.False(await db.TableRows.AnyAsync(r => r.TableInstanceId == s.Document.TableInstanceId).ConfigureAwait(true));
        Assert.False(await db.ApprovalStates.AnyAsync(a => a.DocumentId == id).ConfigureAwait(true));

        // Хто, який документ і скільки комірок: дві комірки клав ArrangeAsync.
        var events = await db.Database
            .SqlQuery<string>($"SELECT ISNULL(DetailsJson, N'') AS Value FROM aud.SecurityEvent WHERE EventType = N'DocumentDeleted' AND ChangedByUserId = {s.UserId}")
            .ToListAsync().ConfigureAwait(true);

        var details = JsonDocument.Parse(Assert.Single(events)).RootElement;
        Assert.Equal(id, details.GetProperty("documentId").GetInt64());
        Assert.Equal(2, details.GetProperty("cells").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Поданий_документ_дає_409_з_причиною_і_лишається_на_місці()
    {
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, GrantLevel.Write, submitted: true)
            .ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);

        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{response.StatusCode}: {body}\n{app.ErrorsText}");

        var problem = JsonDocument.Parse(body).RootElement;
        Assert.Equal("ECR-DOC-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("Submitted", problem.GetProperty("reason").GetString());

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.True(await db.Documents.AnyAsync(d => d.Id == s.Document.DocumentId).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_права_Document_Delete_403()
    {
        var s = await ArrangeAsync("Document.View", GrantLevel.Write).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage6)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Документ_чужого_проєкту_404_як_неіснуючий()
    {
        var s = await ArrangeAsync(DeleteDocumentHandler.Permission, grant: null).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.UserName).ConfigureAwait(true);

        var response = await DeleteAsync(client, s.Document.DocumentId).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        Assert.True(await db.Documents.AnyAsync(d => d.Id == s.Document.DocumentId).ConfigureAwait(true));
    }

    private static Task<HttpResponseMessage> DeleteAsync(HttpClient client, long documentId)
        => client.DeleteAsync(new Uri(
            $"/api/v1/documents/{documentId.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            UriKind.Relative));

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName, password = Password }).ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    /// <summary>Документ із двома комірками і користувач зі своєю роллю.</summary>
    /// <param name="permission">Єдине функціональне право ролі.</param>
    /// <param name="grant">Грант на проєкт документа; <c>null</c> — жодного.</param>
    /// <param name="submitted">Подати аркуш документа за період.</param>
    private async Task<Scenario> ArrangeAsync(string permission, GrantLevel? grant, bool submitted = false)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(columnCount: 1, rowCount: 2).ConfigureAwait(false);

        await using var db = builder.CreateContext();

        foreach (var rowId in document.RowIds)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(document.PeriodKey, rowId, document.ColumnDefIds[0]),
                document.TableDefId,
                new CellValueData { ValueString = "x" }));
        }

        db.DocumentSheets.Add(new DocumentSheet(document.DocumentId, document.SheetDefId));

        if (submitted)
        {
            var state = new ApprovalState(document.DocumentId, document.SheetDefId, document.PeriodKey.Value);
            state.Submit(1, new DateTime(2026, 1, 20, 9, 0, 0, DateTimeKind.Utc));
            db.ApprovalStates.Add(state);
        }

        var userName = $"docdel_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        // Роль своя на кожен прогін: база спільна на всю збірку.
        var role = new Role(
            EcrCode.Create($"DOCDEL_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Doc delete" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, permission));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        if (grant is { } level)
        {
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, document.ProjectId, level));
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(document, userName, user.Id);
    }

    private sealed record Scenario(TestDocument Document, string UserName, int UserId);
}
