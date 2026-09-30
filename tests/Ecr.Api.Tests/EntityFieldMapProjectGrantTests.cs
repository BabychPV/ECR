// tests/Ecr.Api.Tests/EntityFieldMapProjectGrantTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.External;
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
/// S3 аудиту безпеки: мапінг поля джерела на колонку — це запис збором у
/// документи кожного проєкту, що використовує колонку. <c>Integration.Manage</c>
/// без гранта <c>Manage</c> на такий проєкт його не заводить.
/// </summary>
/// <remarks>
/// ⛔ Доказ на HTTP і справжній базі: перелік проєктів колонки рахує запит
/// <c>CollectionStore.FindProjectIdsUsingColumnAsync</c>, а 403 — арм
/// <c>ExceptionHandlingMiddleware</c>. Обробниковий тест не бачить ні того, ні
/// іншого.
/// </remarks>
[Collection("SqlServer")]
public sealed class EntityFieldMapProjectGrantTests(SqlServerFixture sql)
{
    private const string Password = "Map-Grant-Probe-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.11")]
    public async Task S3_мапінг_на_колонку_проєкту_без_гранта_Manage_403_а_з_грантом_200()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None).ConfigureAwait(true);
        var entityId = await SourceEntityAsync().ConfigureAwait(true);
        var column = chain.ColumnDefIds[0];

        try
        {
            using var app = new EcrApiFactory(sql);

            // Integration.Manage і грант Write на проєкт — але не Manage.
            using (var writer = await SignedInAsync(app, chain.ProjectId, GrantLevel.Write).ConfigureAwait(true))
            {
                var denied = await MapAsync(writer, entityId, column).ConfigureAwait(true);

                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
                var problem = await JsonAsync(denied).ConfigureAwait(true);
                Assert.Equal("ECR-AUTH-0403", problem.GetProperty("errorCode").GetString());
                Assert.Equal(
                    "err.ECR-AUTH-0403.noProjectManageGrant", problem.GetProperty("messageKey").GetString());
            }

            // Нічого не записано.
            await using (var db = NewDb())
            {
                Assert.False(
                    await db.EntityFieldMaps.AnyAsync(m => m.SourceEntityId == entityId).ConfigureAwait(true));
            }

            // Контроль: той самий запит із грантом Manage проходить — інакше тест
            // доводив би лише, що маршрут відмовляє завжди.
            using var manager = await SignedInAsync(app, chain.ProjectId, GrantLevel.Manage).ConfigureAwait(true);
            var created = await MapAsync(manager, entityId, column).ConfigureAwait(true);

            Assert.True(created.StatusCode == HttpStatusCode.OK, $"{created.StatusCode}: {app.ErrorsText}");
            Assert.Equal(
                column, (await JsonAsync(created).ConfigureAwait(true)).GetProperty("targetColumnDefId").GetInt32());
        }
        finally
        {
            await using var db = NewDb();
            await db.SourceEntities.Where(e => e.Id == entityId)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.IsActive, false)).ConfigureAwait(true);
        }
    }

    private static Task<HttpResponseMessage> MapAsync(HttpClient client, int entityId, int columnDefId)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/entity-field-maps", UriKind.Relative),
            new
            {
                sourceEntityId = entityId,
                sourceField = "Flare_S3_CO",
                targetKind = "Column",
                targetColumnDefId = columnDefId,
            });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    /// <summary>Вимкнене джерело й активна сутність збору.</summary>
    private async Task<int> SourceEntityAsync()
    {
        await using var db = NewDb();
        var tag = $"{Guid.NewGuid():N}"[..8];

        var source = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("S3"), ExternalTransport.PiWebApi, "https://example.test",
            $"DataSource.Src{tag}");
        source.Deactivate();
        db.DataSources.Add(source);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ⚠ Сутність АКТИВНА: неактивну обробник не знаходить (404). Вимикається
        // в `finally` тесту, інакше `/health/ready` решти прогону — «Degraded».
        var entity = new SourceEntity(source.Id, $"Ent{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return entity.Id;
    }

    /// <summary>Користувач з <c>Integration.Manage</c> і грантом заданого рівня на проєкт.</summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, int projectId, GrantLevel level)
    {
        var name = $"map_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Map grant test"));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Integration.Manage"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, level));
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
