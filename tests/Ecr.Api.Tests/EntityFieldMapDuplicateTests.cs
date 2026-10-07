// tests/Ecr.Api.Tests/EntityFieldMapDuplicateTests.cs
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
/// D-3 приймальної №8: другий мапінг тієї самої пари (сутність, поле) падав на
/// унікальному індексі <c>UQ_EntityFieldMap</c> голим <c>500 ECR-SYS-0500</c>.
/// </summary>
/// <remarks>
/// ⛔ Доказ на HTTP і справжній базі: відмову дає і перевірка обробника, і (у гонці) перехоплення
/// порушення індексу в сховищі; обробниковий тест не бачить ні індексу, ні арма <c>409</c> у middleware.
/// </remarks>
[Collection("SqlServer")]
public sealed class EntityFieldMapDuplicateTests(SqlServerFixture sql)
{
    private const string Password = "Map-Duplicate-Probe-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-3")]
    public async Task D3_другий_мапінг_тієї_самої_пари_сутність_поле_дає_409_з_ключем_а_інша_пара_проходить()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var chain = await builder.BuildAsync(ct: CancellationToken.None).ConfigureAwait(true);
        var first = await SourceEntityAsync().ConfigureAwait(true);
        var second = await SourceEntityAsync().ConfigureAwait(true);
        var column = chain.ColumnDefIds[0];

        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await ManagerAsync(app, chain.ProjectId).ConfigureAwait(true);

            var created = await MapAsync(client, first, "Dup_A", column).ConfigureAwait(true);
            Assert.True(created.StatusCode == HttpStatusCode.OK, $"{created.StatusCode}: {app.ErrorsText}");

            // Та сама пара — 409, а не 500.
            var again = await MapAsync(client, first, "Dup_A", column).ConfigureAwait(true);
            Assert.True(again.StatusCode == HttpStatusCode.Conflict, $"{again.StatusCode}: {app.ErrorsText}");
            var problem = await JsonAsync(again).ConfigureAwait(true);
            Assert.Equal("ECR-INT-0409", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-INT-0409.fieldMapDuplicate", problem.GetProperty("messageKey").GetString());

            // Реєстр поля в індексі нечутливий до регістру (колація бази): інший регістр — це та сама пара.
            var otherCase = await MapAsync(client, first, "dup_a", column).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Conflict, otherCase.StatusCode);

            // Контроль: інше поле тієї ж сутності та те саме поле іншої сутності проходять —
            // інакше тест доводив би лише, що маршрут відмовляє завжди.
            var otherField = await MapAsync(client, first, "Dup_B", column).ConfigureAwait(true);
            Assert.True(otherField.StatusCode == HttpStatusCode.OK, $"{otherField.StatusCode}: {app.ErrorsText}");
            var otherEntity = await MapAsync(client, second, "Dup_A", column).ConfigureAwait(true);
            Assert.True(otherEntity.StatusCode == HttpStatusCode.OK, $"{otherEntity.StatusCode}: {app.ErrorsText}");

            // У базі рівно один мапінг пари (перший), а відмова нічого не дописала.
            await using var db = NewDb();
            Assert.Equal(
                1,
                await db.EntityFieldMaps.CountAsync(m => m.SourceEntityId == first && m.SourceField == "Dup_A")
                    .ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(first, second).ConfigureAwait(true);
        }
    }

    private static Task<HttpResponseMessage> MapAsync(HttpClient client, int entityId, string field, int columnDefId)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/entity-field-maps", UriKind.Relative),
            new
            {
                sourceEntityId = entityId,
                sourceField = field,
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
            EcrCode.Create($"Src{tag}"), Name("D3"), ExternalTransport.PiWebApi, "https://example.test",
            $"DataSource.Src{tag}");
        source.Deactivate();
        db.DataSources.Add(source);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ⚠ Сутність АКТИВНА: неактивну обробник не знаходить (404). Вимикається в `finally` тесту,
        // інакше `/health/ready` решти прогону — «Degraded».
        var entity = new SourceEntity(source.Id, $"Ent{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return entity.Id;
    }

    private async Task DeactivateAsync(params int[] entityIds)
    {
        await using var db = NewDb();
        await db.SourceEntities.Where(e => entityIds.Contains(e.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.IsActive, false)).ConfigureAwait(false);
    }

    /// <summary>Користувач з <c>Integration.Manage</c> і грантом <c>Manage</c> на проєкт колонки.</summary>
    private async Task<HttpClient> ManagerAsync(EcrApiFactory app, int projectId)
    {
        var name = $"dup_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Map duplicate test"));
            db.Users.Add(user);
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, "Integration.Manage"));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, projectId, GrantLevel.Manage));
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
