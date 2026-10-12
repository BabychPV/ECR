// tests/Ecr.Api.Tests/HiddenDocumentUniform404Tests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// EA6-D1 (P2): <c>PATCH /documents/{id}/cells</c> і <c>POST /documents/{id}/rows</c> віддавали РІЗНІ 404 для
/// схованого й неіснуючого документа, коли в тілі — чужий <c>tableInstanceId</c>: схований (з екземпляром
/// самого документа) — <c>document</c>, неіснуючий — «екземпляр не належить документу». Тепер тіло залежить
/// лише від видимості документа з маршруту: схований і неіснуючий — побайтно однакові (з точністю до id).
/// </summary>
/// <remarks>
/// Мутаційний доказ: повернути в <c>CellsController.Patch</c> / <c>CreateRowHandler</c> порівняння
/// <c>instance.DocumentId != documentId</c> ДО перевірки видимості — червоніє рядок «неіснуючий з чужим».
/// </remarks>
[Collection("SqlServer")]
public sealed class HiddenDocumentUniform404Tests(SqlServerFixture sql)
{
    private const string Password = "Api-Uniform-404-2026!";
    private const long MissingDocument = 9_000_000_000_001;
    private const long MissingInstance = 9_000_000_000_002;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "EA6-D1")]
    public async Task Схований_і_неіснуючий_документ_відповідають_однаково_на_PATCH_cells_і_POST_rows()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var visible = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(true);
        var hidden = await builder.BuildAsync(columnCount: 2, rowCount: 1).ConfigureAwait(true);
        var userName = await ArrangeUserAsync(builder, visible.ProjectId).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(true);
        Assert.True(login.IsSuccessStatusCode, $"Вхід: {login.StatusCode}: {app.ErrorsText}");

        // Той самий набір тіл (екземпляр самого документа, чужий існуючий, відсутній) для схованого й
        // неіснуючого документа з маршруту — а також для ВИДИМОГО документа з чужим/відсутнім екземпляром.
        var instances = new[] { hidden.TableInstanceId, visible.TableInstanceId, MissingInstance };

        foreach (var write in new Func<HttpClient, long, long, long, Task<(HttpStatusCode, string)>>[] { PatchAsync, PostRowAsync })
        {
            var baseline = await write(client, hidden.DocumentId, hidden.TableInstanceId, hidden.DocumentId)
                .ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.NotFound, baseline.Item1);
            Assert.Equal("err.ECR-DOC-0404.document", JsonDocument.Parse(baseline.Item2).RootElement.GetProperty("messageKey").GetString());
            var expected = Normalized(baseline.Item2, hidden.DocumentId);

            foreach (var route in new[] { hidden.DocumentId, MissingDocument })
            {
                foreach (var instance in instances)
                {
                    var response = await write(client, route, instance, route).ConfigureAwait(true);
                    Assert.True(
                        response.Item1 == HttpStatusCode.NotFound,
                        $"route={route} instance={instance}: {response.Item1}: {response.Item2}\n{app.ErrorsText}");
                    Assert.Equal(expected, Normalized(response.Item2, route));
                }
            }

            // Видимий документ: чужий (схований від читача) і відсутній екземпляр — те саме тіло (D3-A S-4).
            foreach (var instance in new[] { hidden.TableInstanceId, MissingInstance })
            {
                var response = await write(client, visible.DocumentId, instance, visible.DocumentId).ConfigureAwait(true);
                Assert.True(
                    response.Item1 == HttpStatusCode.NotFound,
                    $"visible instance={instance}: {response.Item1}: {response.Item2}\n{app.ErrorsText}");
                Assert.Equal(expected, Normalized(response.Item2, visible.DocumentId));
            }
        }
    }
    /// <summary>Поля відповіді, що мусять збігатися; id документа з маршруту — заступником.</summary>
    private static string Normalized(string body, long routeDocumentId)
    {
        var root = JsonDocument.Parse(body).RootElement;
        var parts = new List<string>();
        foreach (var name in new[] { "status", "errorCode", "messageKey", "title", "detail", "documentId", "tableInstanceId" })
        {
            parts.Add(root.TryGetProperty(name, out var value) ? $"{name}={value}" : $"{name}=<немає>");
        }

        return string.Join("|", parts).Replace(
            routeDocumentId.ToString(System.Globalization.CultureInfo.InvariantCulture), "<doc>", StringComparison.Ordinal);
    }

    private static async Task<(HttpStatusCode, string)> PatchAsync(
        HttpClient client, long documentId, long tableInstanceId, long _)
    {
        using var response = await client.PatchAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/cells", UriKind.Relative),
            new
            {
                tableInstanceId,
                periodKey = 202601,
                origin = "UserEdit",
                rows = new[]
                {
                    new
                    {
                        rowKey = $"INV{Guid.NewGuid():N}"[..12],
                        baseVersion = (string?)null,
                        cells = new object[] { new { columnCode = "C1", value = (object)1m } },
                    },
                },
            }).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static async Task<(HttpStatusCode, string)> PostRowAsync(
        HttpClient client, long documentId, long tableInstanceId, long _)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/documents/{documentId}/rows", UriKind.Relative),
            new { tableInstanceId, rowKey = $"INV{Guid.NewGuid():N}"[..12] }).ConfigureAwait(false);

        return (response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    /// <summary>Користувач із Write лише на проєкт видимого документа; другого проєкту він не бачить.</summary>
    private static async Task<string> ArrangeUserAsync(TestDocumentBuilder builder, int visibleProjectId)
    {
        await using var db = builder.CreateContext();

        var name = $"u404_{Guid.NewGuid():N}"[..20];
        var user = new User(name, name, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        db.Users.Add(user);

        var role = new Role(
            EcrCode.Create($"U404_{Guid.NewGuid():N}"[..24]),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "u404" }));
        db.Roles.Add(role);
        await db.SaveChangesAsync().ConfigureAwait(false);

        db.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, visibleProjectId, GrantLevel.Write));
        db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        await db.SaveChangesAsync().ConfigureAwait(false);

        return name;
    }
}