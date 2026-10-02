using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// T1-01 наскрізно (HTTP, справжній SQL): текст Check містить значення ОБОХ сторін зв'язку, тож читач, якому
/// заборонено колонку чи таблицю ДЖЕРЕЛА, не бачить його ні в <c>POST /validate</c>, ні в <c>GET /validation</c>
/// (зокрема для підсумку, збереженого ДО фіксу, без полів джерела).
/// </summary>
/// <remarks>
/// Мутація: у <c>HiddenValidationIssues.CanSee</c> прибрати перевірку джерела — тест червоніє.
/// </remarks>
[Collection("SqlServer")]
public sealed class CheckSourceDenyValidationTests(SqlServerFixture sql)
{
    private const string Password = "Api-Check-Source-Deny-2026!";
    private const decimal Secret = 777.5m;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Check_із_забороненим_джерелом_не_віддає_значень_у_validate_і_validation_навіть_для_старого_підсумку()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 3, rowCount: 1).ConfigureAwait(true);
        var extra = (await MultiTableDocument.AddTablesAsync(builder, doc, [1], columnCount: 1, rowCount: 1).ConfigureAwait(true))[0];

        await using var db = builder.CreateContext();
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, doc.SheetDefId));
        db.DocumentSheets.Add(new DocumentSheet(doc.DocumentId, extra.SheetDefId));
        var leftColumn = doc.ColumnDefIds[1];
        var leftCode = await db.ColumnDefs.Where(c => c.Id == leftColumn).Select(c => c.Code).SingleAsync().ConfigureAwait(true);
        var rightColumn = extra.ColumnDefIds[0];
        db.CellValues.Add(new CellValue(new CellAddress(doc.PeriodKey, doc.RowIds[0], leftColumn), doc.TableDefId, new CellValueData { ValueNumeric = Secret }));
        db.CellValues.Add(new CellValue(new CellAddress(doc.PeriodKey, extra.RowIds[0], rightColumn), extra.TableDefId, new CellValueData { ValueNumeric = 55m }));

        var relation = new TableRelationDef(EcrCode.Create("CHK1"), doc.TableDefId, extra.TableDefId, TableRelationKind.Check, "{}");
        relation.Update(
            doc.TableDefId, extra.TableDefId, TableRelationKind.Check, "{}",
            $$"""{"left":"{{leftCode}}","right":"{{extra.ColumnCodes[0]}}","tolerance":"0","severity":"Block"}""", 0, true);
        db.TableRelations.Add(relation);

        var viewer = NewRole("CSD_V");
        db.Roles.Add(viewer);
        var users = new Dictionary<string, User>
        {
            ["plain"] = NewUser("csp"), ["column"] = NewUser("csc"), ["table"] = NewUser("cst"),
        };
        db.Users.AddRange(users.Values);
        await db.SaveChangesAsync().ConfigureAwait(true);

        db.RolePermissions.Add(new RolePermission(viewer.Id, "Document.View"));
        db.ResourceGrants.Add(new ResourceGrant(viewer.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Read));
        foreach (var user in users.Values)
        {
            db.RoleAssignments.Add(new RoleAssignment(viewer.Id, user.Id, null));
        }

        foreach (var (key, kind, id) in new[] { ("column", ResourceKind.Column, leftColumn), ("table", ResourceKind.Table, doc.TableDefId) })
        {
            var denier = NewRole("CSD_D");
            db.Roles.Add(denier);
            await db.SaveChangesAsync().ConfigureAwait(true);
            db.RoleAssignments.Add(new RoleAssignment(denier.Id, users[key].Id, null));
            db.ResourceGrants.Add(new ResourceGrant(denier.Id, kind, id, GrantLevel.Read, isDeny: true));
        }

        await db.SaveChangesAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        var period = doc.PeriodKey.Value;
        foreach (var (key, user) in users)
        {
            using var client = app.CreateClient();
            var login = await client.PostAsJsonAsync(
                new Uri("/api/v1/login/local", UriKind.Relative), new { userName = user.UserName, password = Password }).ConfigureAwait(true);
            Assert.True(login.IsSuccessStatusCode, $"Вхід {key}: {login.StatusCode}");

            var fresh = await client.PostAsJsonAsync(
                new Uri($"/api/v1/documents/{doc.DocumentId}/validate", UriKind.Relative), new { periodKey = period }).ConfigureAwait(true);
            var freshBody = await fresh.Content.ReadAsStringAsync().ConfigureAwait(true);

            var stored = await client.GetAsync(
                new Uri($"/api/v1/documents/{doc.DocumentId}/validation?periodKey={period}", UriKind.Relative)).ConfigureAwait(true);
            var storedBody = await stored.Content.ReadAsStringAsync().ConfigureAwait(true);

            // Підсумок, збережений ДО фіксу: без полів джерела.
            await StripSourceAsync(doc.DocumentId).ConfigureAwait(true);
            var legacy = await client.GetAsync(
                new Uri($"/api/v1/documents/{doc.DocumentId}/validation?periodKey={period}", UriKind.Relative)).ConfigureAwait(true);
            var legacyBody = await legacy.Content.ReadAsStringAsync().ConfigureAwait(true);

            foreach (var body in new[] { freshBody, storedBody, legacyBody })
            {
                Assert.Equal(key == "plain", body.Contains("777.5", StringComparison.Ordinal));
                Assert.Equal(key == "plain", body.Contains("REL-CHK1", StringComparison.Ordinal));
            }
        }
    }

    private async Task StripSourceAsync(long documentId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        string json;
        object id;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT TOP 1 * FROM wf.ValidationResult WHERE DocumentId = @d ORDER BY RunAt DESC";
            read.Parameters.AddWithValue("@d", documentId);
            await using var reader = await read.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.True(await reader.ReadAsync().ConfigureAwait(false));
            id = reader["Id"];
            json = (string)reader["MessagesJson"];
        }

        var messages = JsonNode.Parse(json)!.AsArray();
        foreach (var message in messages)
        {
            message!.AsObject().Remove("SourceTableDefId");
            message.AsObject().Remove("SourceColumnCode");
        }

        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE wf.ValidationResult SET MessagesJson = @j WHERE Id = @i";
        update.Parameters.AddWithValue("@j", messages.ToJsonString());
        update.Parameters.AddWithValue("@i", id);
        await update.ExecuteNonQueryAsync().ConfigureAwait(false);
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
}
