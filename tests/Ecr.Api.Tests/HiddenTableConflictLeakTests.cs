// tests/Ecr.Api.Tests/HiddenTableConflictLeakTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Приймальна №8, D-6: користувач БЕЗ доступу до схованої таблиці отримував
/// <c>409 ECR-ROW-0409</c> з КОДОМ таблиці та режимом рядків («Table "HT" has RowMode = Fixed…»)
/// на <c>PATCH /cells</c> і <c>POST /rows</c>: перевірка режиму йшла раніше за перевірку видимості.
/// Тепер схована таблиця для читача НЕ ІСНУЄ: ТА САМА відповідь, що й на читання зрізу
/// (<c>404 ECR-DOC-0404 tableInstance</c>); читач, який таблицю бачить, відповідь має попередню.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати <c>DocumentVisibility.RequireTableVisibleAsync</c> з
/// <c>PatchCellsHandler</c>/<c>CreateRowHandler</c> — червоніють рядки <c>hidden</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class HiddenTableConflictLeakTests(SqlServerFixture sql)
{
    private const string Password = "Api-Hidden-409-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-6")]
    public async Task Схована_таблиця_не_видає_свій_код_у_409_а_видима_видає_як_раніше()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        // ── Читач БЕЗ доступу до таблиці: 404, ніде немає коду таблиці й режиму ──
        using (var hidden = await SignedInAsync(app, s.HiddenUser).ConfigureAwait(true))
        {
            foreach (var response in new[]
                     {
                         await PatchInventedRowAsync(hidden, s, s.Hidden.TableInstanceId, s.Hidden.ColumnCodes[0]).ConfigureAwait(true),
                         await PostRowAsync(hidden, s, s.Hidden.TableInstanceId).ConfigureAwait(true),
                     })
            {
                Assert.True(
                    response.Status == HttpStatusCode.NotFound,
                    $"{response.Status}: {response.Body}\n{app.ErrorsText}");

                var problem = JsonDocument.Parse(response.Body).RootElement;
                Assert.Equal("ECR-DOC-0404", problem.GetProperty("errorCode").GetString());
                Assert.Equal("err.ECR-DOC-0404.tableInstance", problem.GetProperty("messageKey").GetString());
                Assert.DoesNotContain(s.Hidden.TableCode, response.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("RowMode", response.Body, StringComparison.Ordinal);
            }
        }

        // ── Регресія: ту саму відмову видима таблиця дає як раніше — з кодом і режимом ──
        using (var plain = await SignedInAsync(app, s.PlainUser).ConfigureAwait(true))
        {
            var patch = await PatchInventedRowAsync(plain, s, s.Hidden.TableInstanceId, s.Hidden.ColumnCodes[0]).ConfigureAwait(true);
            Assert.True(patch.Status == HttpStatusCode.Conflict, $"{patch.Status}: {patch.Body}\n{app.ErrorsText}");
            Assert.Equal("ECR-ROW-0409", JsonDocument.Parse(patch.Body).RootElement.GetProperty("errorCode").GetString());
            Assert.Contains(s.Hidden.TableCode, patch.Body, StringComparison.Ordinal);

            var post = await PostRowAsync(plain, s, s.Hidden.TableInstanceId).ConfigureAwait(true);
            Assert.True(post.Status == HttpStatusCode.Conflict, $"{post.Status}: {post.Body}\n{app.ErrorsText}");
            Assert.Equal("ECR-ROW-0409", JsonDocument.Parse(post.Body).RootElement.GetProperty("errorCode").GetString());
            Assert.Contains(s.Hidden.TableCode, post.Body, StringComparison.Ordinal);
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> PatchInventedRowAsync(
        HttpClient client, Scenario s, long tableInstanceId, string columnCode)
    {
        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId,
                periodKey = s.Doc.PeriodKey.Value,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = $"INV{Guid.NewGuid():N}"[..12],
                        baseVersion = (string?)null,
                        cells = new object[] { new { columnCode, value = (object)1m } },
                    },
                },
            }).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static async Task<(HttpStatusCode Status, string Body)> PostRowAsync(
        HttpClient client, Scenario s, long tableInstanceId)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{s.Doc.DocumentId}/rows", UriKind.Relative),
            new { tableInstanceId, rowKey = $"INV{Guid.NewGuid():N}"[..12] }).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
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
    /// Документ на дві Fixed-таблиці (основна й ще одна на новому аркуші) і два користувачі з
    /// <c>Write</c> на проєкт: <c>hidden</c> із забороною (Deny) на другу таблицю, <c>plain</c> без неї.
    /// </summary>
    private async Task<Scenario> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var doc = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(false);
        var extra = await MultiTableDocument.AddTablesAsync(builder, doc, [1]).ConfigureAwait(false);
        var hiddenTable = extra[0];

        await using var db = builder.CreateContext();

        var hidden = NewUser("hid");
        var plain = NewUser("pln");
        db.Users.AddRange(hidden, plain);

        var writer = NewRole("H409_W");
        var denier = NewRole("H409_D");
        db.Roles.AddRange(writer, denier);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(writer.Id, "Document.View"));
        db.ResourceGrants.Add(new ResourceGrant(writer.Id, ResourceKind.Project, doc.ProjectId, GrantLevel.Write));
        db.RoleAssignments.Add(new RoleAssignment(writer.Id, hidden.Id, null));
        db.RoleAssignments.Add(new RoleAssignment(writer.Id, plain.Id, null));

        db.RoleAssignments.Add(new RoleAssignment(denier.Id, hidden.Id, null));
        db.ResourceGrants.Add(new ResourceGrant(denier.Id, ResourceKind.Table, hiddenTable.TableDefId, GrantLevel.Read, isDeny: true));

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Scenario(doc, hiddenTable, hidden.UserName, plain.UserName);
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

    private sealed record Scenario(TestDocument Doc, ExtraTable Hidden, string HiddenUser, string PlainUser);
}
