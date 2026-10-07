// tests/Ecr.Api.Tests/EntityFieldMapUnitDimensionTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Units;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// D-4 приймальної №8: мапінг із несумісними розмірностями одиниць (MJ → kg) приймався з <c>200</c>, а
/// перегляд мапінгу давав <c>Materialized</c> з порожнім значенням без жодної причини.
/// </summary>
/// <remarks>
/// ⛔ Рішення D-4: варіант А. Обидві одиниці відомі на момент створення мапінгу (<c>sourceUnitId</c> і
/// <c>targetUnitId</c> — поля самого мапінгу), тож несумісність відхиляється одразу (<c>422 ECR-UOM-0422</c>).
/// Наявні мапінги не чіпаються (без міграції даних) - для них перегляд чесно називає причину (<c>unitIssue</c>).
/// Інтеграл за часом пропускається: «швидкість × с» → величина — законний перехід між розмірностями.
/// </remarks>
[Collection("SqlServer")]
public sealed class EntityFieldMapUnitDimensionTests(SqlServerFixture sql)
{
    private const string Password = "Map-Unit-Probe-2026!";

    private const string MismatchKey = "err.ECR-UOM-0422.fieldMapUnitDimensions";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-4")]
    public async Task D4_створення_мапінгу_з_несумісними_розмірностями_422_а_сумісні_й_інтеграл_проходять()
    {
        var chain = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None)
            .ConfigureAwait(true);
        var entityId = await SourceEntityAsync().ConfigureAwait(true);
        var units = await UnitsAsync().ConfigureAwait(true);

        try
        {
            using var app = new EcrApiFactory(sql);
            using var client = await ManagerAsync(app, chain.ProjectId).ConfigureAwait(true);

            // MJ -> kg: різні розмірності.
            var mismatch = await CreateAsync(client, entityId, "U_mismatch", chain.ColumnDefIds[0], units.Energy, units.Mass, null)
                .ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, mismatch.StatusCode);
            var problem = await JsonAsync(mismatch).ConfigureAwait(true);
            Assert.Equal("ECR-UOM-0422", problem.GetProperty("errorCode").GetString());
            Assert.Equal(MismatchKey, problem.GetProperty("messageKey").GetString());

            await using (var db = NewDb())
            {
                Assert.False(
                    await db.EntityFieldMaps.AnyAsync(m => m.SourceEntityId == entityId).ConfigureAwait(true));
            }

            // Контроль: kg -> t (одна розмірність) проходить - інакше тест доводив би лише, що маршрут відмовляє завжди.
            var compatible = await CreateAsync(client, entityId, "U_ok", chain.ColumnDefIds[0], units.Mass, units.MassOther, null)
                .ConfigureAwait(true);
            Assert.True(compatible.StatusCode == HttpStatusCode.OK, $"{compatible.StatusCode}: {app.ErrorsText}");

            // Інтеграл за часом: перехід між розмірностями законний (швидкість x с -> величина) і приймається.
            var integral = await CreateAsync(client, entityId, "U_integral", chain.ColumnDefIds[0], units.Energy, units.Mass, "TimeIntegral")
                .ConfigureAwait(true);
            Assert.True(integral.StatusCode == HttpStatusCode.OK, $"{integral.StatusCode}: {app.ErrorsText}");
        }
        finally
        {
            await DeactivateAsync(entityId).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-4")]
    public async Task D4_наявний_несумісний_мапінг_не_ламається_а_перегляд_називає_причину_замість_мовчазного_null()
    {
        var chain = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None)
            .ConfigureAwait(true);
        var entityId = await SourceEntityAsync().ConfigureAwait(true);
        var units = await UnitsAsync().ConfigureAwait(true);

        try
        {
            // Мапінг, заведений напряму в базі - «до фіксу».
            await using (var db = NewDb())
            {
                var legacy = EntityFieldMap.ToColumn(entityId, "Legacy_MJ_kg", chain.ColumnDefIds[0]);
                legacy.SetUnits(units.Energy, units.Mass);
                legacy.SetMaterialization("R1", AggregationKind.Sum);
                var good = EntityFieldMap.ToColumn(entityId, "Good_kg_t", chain.ColumnDefIds[0]);
                good.SetUnits(units.Mass, units.MassOther);
                good.SetMaterialization("R2", AggregationKind.Sum);
                db.EntityFieldMaps.AddRange(legacy, good);
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            using var app = new EcrApiFactory(sql);
            using var client = await ManagerAsync(app, chain.ProjectId).ConfigureAwait(true);

            var response = await client.GetAsync(new Uri($"/api/v1/sources/{entityId}/mapping/preview", UriKind.Relative))
                .ConfigureAwait(true);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {app.ErrorsText}");

            var fields = (await JsonAsync(response).ConfigureAwait(true)).GetProperty("fields").EnumerateArray().ToList();
            var bad = fields.Single(f => f.GetProperty("sourceField").GetString() == "Legacy_MJ_kg");
            var fine = fields.Single(f => f.GetProperty("sourceField").GetString() == "Good_kg_t");

            // Не мовчазний null: причина названа ключем каталогу.
            Assert.Equal(MismatchKey, bad.GetProperty("unitIssue").GetString());
            Assert.Equal(JsonValueKind.Null, fine.GetProperty("unitIssue").ValueKind);
        }
        finally
        {
            await DeactivateAsync(entityId).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-4")]
    public async Task D4_прийняття_нової_одиниці_джерела_іншої_розмірності_ніж_ціль_422_а_тієї_самої_200()
    {
        var chain = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(ct: CancellationToken.None)
            .ConfigureAwait(true);
        var entityId = await SourceEntityAsync().ConfigureAwait(true);
        var units = await UnitsAsync().ConfigureAwait(true);
        int mapId;

        try
        {
            await using (var db = NewDb())
            {
                var map = EntityFieldMap.ToColumn(entityId, "Accept_kg_t", chain.ColumnDefIds[0]);
                map.SetUnits(units.Mass, units.MassOther);
                map.SetMaterialization("R1", AggregationKind.Sum);
                db.EntityFieldMaps.Add(map);
                await db.SaveChangesAsync().ConfigureAwait(true);
                mapId = map.Id;
            }

            using var app = new EcrApiFactory(sql);
            using var client = await ManagerAsync(app, chain.ProjectId).ConfigureAwait(true);

            var refused = await client.PostAsJsonAsync(
                new Uri($"/api/v1/entity-field-maps/{mapId}/accept-unit-change", UriKind.Relative),
                new { sourceUnitId = units.Energy });
            Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
            var problem = await JsonAsync(refused).ConfigureAwait(true);
            Assert.Equal(MismatchKey, problem.GetProperty("messageKey").GetString());

            await using (var db = NewDb())
            {
                Assert.Equal(
                    units.Mass,
                    await db.EntityFieldMaps.Where(m => m.Id == mapId).Select(m => m.SourceUnitId).SingleAsync()
                        .ConfigureAwait(true));
            }

            // Контроль: одиниця тієї самої розмірності як ціль приймається.
            var third = await UnitAsync(units.MassDimension, "kg3", 0.001m).ConfigureAwait(true);
            var accepted = await client.PostAsJsonAsync(
                new Uri($"/api/v1/entity-field-maps/{mapId}/accept-unit-change", UriKind.Relative),
                new { sourceUnitId = third });
            Assert.True(accepted.StatusCode == HttpStatusCode.OK, $"{accepted.StatusCode}: {app.ErrorsText}");
        }
        finally
        {
            await DeactivateAsync(entityId).ConfigureAwait(true);
        }
    }

    private static Task<HttpResponseMessage> CreateAsync(
        HttpClient client, int entityId, string field, int columnDefId, int sourceUnitId, int targetUnitId, string? aggregation)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/entity-field-maps", UriKind.Relative),
            new
            {
                sourceEntityId = entityId,
                sourceField = field,
                targetKind = "Column",
                targetColumnDefId = columnDefId,
                sourceUnitId,
                targetUnitId,
                targetRowKey = aggregation is null ? null : "R1",
                aggregation,
            });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private sealed record Units(int Mass, int MassOther, byte MassDimension, int Energy);

    /// <summary>Дві одиниці однієї розмірності (маса: kg, t) і одна іншої (енергія: MJ).</summary>
    private async Task<Units> UnitsAsync()
    {
        await using var db = NewDb();
        var dims = await db.Dimensions.OrderBy(d => d.Id).Select(d => d.Id).Take(2).ToListAsync().ConfigureAwait(false);

        var mass = await UnitAsync(dims[0], "kg", 1m).ConfigureAwait(false);
        var massOther = await UnitAsync(dims[0], "t", 1000m).ConfigureAwait(false);
        var energy = await UnitAsync(dims[1], "MJ", 1m).ConfigureAwait(false);

        return new Units(mass, massOther, dims[0], energy);
    }

    private async Task<int> UnitAsync(byte dimensionId, string prefix, decimal factor)
    {
        await using var db = NewDb();
        var tag = $"{Guid.NewGuid():N}"[..8];
        var unit = new Unit(
            EcrCode.Create($"{prefix}{tag}"), Name(prefix), Name(prefix), dimensionId,
            isBase: false, factorToBase: factor, offsetToBase: 0m);
        db.Units.Add(unit);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return unit.Id;
    }

    /// <summary>Вимкнене джерело й активна сутність збору.</summary>
    private async Task<int> SourceEntityAsync()
    {
        await using var db = NewDb();
        var tag = $"{Guid.NewGuid():N}"[..8];

        var source = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("D4"), ExternalTransport.PiWebApi, "https://example.test",
            $"DataSource.Src{tag}");
        source.Deactivate();
        db.DataSources.Add(source);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // ⚠ Сутність АКТИВНА: неактивну обробник не знаходить (404). Вимикається в `finally` тесту,
        // інакше `/health/ready` решти прогону - «Degraded».
        var entity = new SourceEntity(source.Id, $"Ent{tag}", RegistrySourceKind.External);
        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return entity.Id;
    }

    private async Task DeactivateAsync(int entityId)
    {
        await using var db = NewDb();
        await db.SourceEntities.Where(e => e.Id == entityId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.IsActive, false)).ConfigureAwait(false);
    }

    /// <summary>Користувач з <c>Integration.Manage</c> і грантом <c>Manage</c> на проєкт колонки.</summary>
    private async Task<HttpClient> ManagerAsync(EcrApiFactory app, int projectId)
    {
        var name = $"unt_{Guid.NewGuid():N}"[..20];

        await using (var db = NewDb())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Map unit test"));
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
