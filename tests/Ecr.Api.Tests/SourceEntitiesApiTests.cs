// tests/Ecr.Api.Tests/SourceEntitiesApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Сутність збору з вебу (<c>ФВ-13.11</c>), прив'язка до довідника і
/// перевірки мапінгу на поле довідника (<c>ФВ-8.11</c>) — на HTTP.
/// </summary>
/// <remarks>
/// ⛔ Доказ саме на HTTP: дубль і конфлікт стану мапить у <c>409</c> окремий арм
/// <c>ExceptionHandlingMiddleware</c> (<c>BusinessRuleException</c> з
/// <c>ECR-INT-0409</c>) — обробниковий тест лишався б зеленим, поки клієнт
/// бачить <c>422</c>.
///
/// ⚠ Кожна створена тут сутність наприкінці ВИМИКАЄТЬСЯ: база тестів спільна,
/// і активна сутність без завершеного збору — прогалина для
/// <c>SourcesHealthCheck</c> (та сама пастка, що в
/// <c>EntityFieldMapLifecycleTests</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class SourceEntitiesApiTests(SqlServerFixture sql)
{
    private static readonly Uri Sources = new("/api/v1/sources", UriKind.Relative);

    private static string Manage => Ecr.Application.Sources.CreateSourceEntityHandler.Permission;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.11")]
    public async Task Сутність_заводиться_201_а_дубль_коду_дає_409()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage).ConfigureAwait(true);
        var dataSourceId = await DataSourceAsync().ConfigureAwait(true);
        var code = $"Ent{Guid.NewGuid():N}"[..14];

        try
        {
            var created = await client.PostAsJsonAsync(
                Sources, new { dataSourceId, code, displayName = "Flare 01", entityPath = @"\\AF\ECR\" + code });

            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var dto = await JsonAsync(created).ConfigureAwait(true);
            Assert.Equal(code, dto.GetProperty("code").GetString());
            Assert.Equal("External", dto.GetProperty("sourceKind").GetString());

            // Рядок у базі, а не лише у відповіді.
            Assert.True(await ExistsAsync(dataSourceId, code).ConfigureAwait(true));

            // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати перевірку `SourceEntityCodeExistsAsync`
            // — відповідає не 409, а збій запису об UQ_SourceEntity.
            var duplicate = await client.PostAsJsonAsync(Sources, new { dataSourceId, code });

            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            var problem = await JsonAsync(duplicate).ConfigureAwait(true);
            Assert.Equal("ECR-INT-0409", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-INT-0409.sourceEntityDuplicate", problem.GetProperty("messageKey").GetString());
        }
        finally
        {
            await DeactivateAsync(dataSourceId).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-13.11")]
    public async Task Без_права_Integration_Manage_заведення_і_прив_язка_дають_403()
    {
        // ⚠ Автентифікований зі СТОРОННІМ правом — не анонім.
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Integration.View").ConfigureAwait(true);
        var dataSourceId = await DataSourceAsync().ConfigureAwait(true);
        var stand = await StandAsync(dataSourceId).ConfigureAwait(true);

        try
        {
            var create = await client.PostAsJsonAsync(Sources, new { dataSourceId, code = "Denied" });
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
            Assert.False(await ExistsAsync(dataSourceId, "Denied").ConfigureAwait(true));

            var bind = await client.PutAsJsonAsync(
                new Uri($"/api/v1/sources/{stand.EntityId}/registry", UriKind.Relative),
                new { registryDefId = stand.RegistryId });
            Assert.Equal(HttpStatusCode.Forbidden, bind.StatusCode);
            Assert.Null(await BoundRegistryAsync(stand.EntityId).ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(dataSourceId).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Прив_язка_до_довідника_обмежує_мапінг_полями_саме_цього_довідника()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage).ConfigureAwait(true);
        var dataSourceId = await DataSourceAsync().ConfigureAwait(true);
        var stand = await StandAsync(dataSourceId).ConfigureAwait(true);
        var registryUri = new Uri($"/api/v1/sources/{stand.EntityId}/registry", UriKind.Relative);

        try
        {
            // Поки не прив'язана — поле довідника не мапиться.
            var unbound = await MapAsync(client, stand.EntityId, stand.OwnFieldId).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unbound.StatusCode);
            Assert.Equal(
                "err.ECR-REQ-0422.entityFieldMapRegistryNotBound",
                (await JsonAsync(unbound).ConfigureAwait(true)).GetProperty("messageKey").GetString());

            var missing = await client.PutAsJsonAsync(registryUri, new { registryDefId = int.MaxValue });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(
                "err.ECR-REG-0404.registryId",
                (await JsonAsync(missing).ConfigureAwait(true)).GetProperty("messageKey").GetString());

            var bound = await client.PutAsJsonAsync(registryUri, new { registryDefId = stand.RegistryId });
            Assert.Equal(HttpStatusCode.OK, bound.StatusCode);
            Assert.Equal(stand.RegistryId, await BoundRegistryAsync(stand.EntityId).ConfigureAwait(true));

            // Перелік несе прив'язку — з неї клієнт показує поточний довідник.
            var list = await JsonAsync(await client.GetAsync(Sources).ConfigureAwait(true)).ConfigureAwait(true);
            var row = list.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == stand.EntityId);
            Assert.Equal(stand.RegistryId, row.GetProperty("registryDefId").GetInt32());

            var foreign = await MapAsync(client, stand.EntityId, stand.ForeignFieldId).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, foreign.StatusCode);
            var foreignProblem = await JsonAsync(foreign).ConfigureAwait(true);
            Assert.Equal("ECR-REQ-0422", foreignProblem.GetProperty("errorCode").GetString());
            Assert.Equal(
                "err.ECR-REQ-0422.entityFieldMapRegistryFieldForeign",
                foreignProblem.GetProperty("messageKey").GetString());

            var withRow = await MapAsync(
                client, stand.EntityId, stand.OwnFieldId, targetRowKey: "R1", aggregation: "Sum").ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, withRow.StatusCode);
            Assert.Equal(
                "err.ECR-REQ-0422.entityFieldMapRegistryFieldMaterialization",
                (await JsonAsync(withRow).ConfigureAwait(true)).GetProperty("messageKey").GetString());

            // Контроль: власне поле без рядка й агрегації — заводиться.
            var own = await MapAsync(client, stand.EntityId, stand.OwnFieldId).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);

            var unbind = await client.PutAsJsonAsync(registryUri, new { registryDefId = (int?)null });
            Assert.Equal(HttpStatusCode.OK, unbind.StatusCode);
            Assert.Null(await BoundRegistryAsync(stand.EntityId).ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(dataSourceId).ConfigureAwait(true);
        }
    }

    /// <summary>Сутність і два довідники по одному полю.</summary>
    private sealed record Stand(int EntityId, int RegistryId, int OwnFieldId, int ForeignFieldId);

    private static Task<HttpResponseMessage> MapAsync(
        HttpClient client, int entityId, int fieldId, string? targetRowKey = null, string? aggregation = null)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/entity-field-maps", UriKind.Relative),
            new
            {
                sourceEntityId = entityId,
                sourceField = $"F{fieldId}",
                targetKind = "RegistryField",
                targetRegistryFieldDefId = fieldId,
                targetRowKey,
                aggregation,
            });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private async Task<int> DataSourceAsync()
    {
        await using var db = new EcrDbContext(Options());
        var tag = $"{Guid.NewGuid():N}"[..8];

        var source = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("ФВ-13.11"), ExternalTransport.PiWebApi,
            "https://example.test", "secret");
        db.DataSources.Add(source);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return source.Id;
    }

    private async Task<Stand> StandAsync(int dataSourceId)
    {
        await using var db = new EcrDbContext(Options());
        var tag = $"{Guid.NewGuid():N}"[..8];

        var entity = new SourceEntity(dataSourceId, $"Ent{tag}", RegistrySourceKind.External);
        var own = new RegistryDef(EcrCode.Create($"RSE{tag}"), Name("own"), isTemporal: false);
        var other = new RegistryDef(EcrCode.Create($"RSF{tag}"), Name("other"), isTemporal: false);

        db.SourceEntities.Add(entity);
        db.RegistryDefs.AddRange(own, other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var ownField = new RegistryFieldDef(own.Id, EcrCode.Create("Limit"), Name("Limit"), CellDataType.Decimal, 1);
        var foreignField = new RegistryFieldDef(other.Id, EcrCode.Create("Limit"), Name("Limit"), CellDataType.Decimal, 1);
        db.RegistryFieldDefs.AddRange(ownField, foreignField);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(entity.Id, own.Id, ownField.Id, foreignField.Id);
    }

    private async Task<bool> ExistsAsync(int dataSourceId, string code)
    {
        await using var db = new EcrDbContext(Options());

        return await db.SourceEntities.AsNoTracking()
            .AnyAsync(e => e.DataSourceId == dataSourceId && e.Code == code).ConfigureAwait(false);
    }

    private async Task<int?> BoundRegistryAsync(int entityId)
    {
        await using var db = new EcrDbContext(Options());

        return await db.SourceEntities.AsNoTracking()
            .Where(e => e.Id == entityId).Select(e => e.RegistryDefId).SingleAsync().ConfigureAwait(false);
    }

    /// <summary>Вимикає всі сутності з'єднання — спільна база не має лишитися з «прогалиною».</summary>
    private async Task DeactivateAsync(int dataSourceId)
    {
        await using var db = new EcrDbContext(Options());

        var entities = await db.SourceEntities.Where(e => e.DataSourceId == dataSourceId).ToListAsync()
            .ConfigureAwait(false);
        entities.ForEach(e => e.Deactivate());
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
        => SystemHealthControllerTests.SignedInAsync(sql, app, permissions);
}
