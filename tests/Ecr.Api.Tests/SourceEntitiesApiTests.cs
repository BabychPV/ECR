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

            // ⚠ Мутаційно НЕ доведено (класифікатор / рішення людини 2026-09-28).
            // Очікування, не перевірене прогоном: без перевірки
            // `SourceEntityCodeExistsAsync` відповідь була б не 409, а збій запису
            // об UQ_SourceEntity.
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
        // ⚠ Registry.EditData — прив'язка вимагає права на дані довідника
        // (D-202, доповнення 2026-09-29; відмова без нього —
        // SourceBindRegistryPermissionTests).
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage, "Registry.EditData").ConfigureAwait(true);
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

    /// <summary>
    /// L4-07 (D-202): мапінг на поле довідника — запис збором у довідник. <c>Integration.Manage</c> без права на дані
    /// довідника не заводить, не призупиняє, не відновлює й не видаляє його. До виправлення всі чотири дії давали 200/204.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task L4_07_мапінг_на_поле_довідника_без_права_на_дані_довідника_403_на_заведення_паузу_відновлення_і_видалення()
    {
        using var app = new EcrApiFactory(sql);
        using var editor = await SignedInAsync(app, Manage, "Registry.EditData").ConfigureAwait(true);
        using var managerOnly = await SignedInAsync(app, Manage).ConfigureAwait(true);
        var dataSourceId = await DataSourceAsync().ConfigureAwait(true);
        var stand = await StandAsync(dataSourceId).ConfigureAwait(true);

        try
        {
            var bound = await editor.PutAsJsonAsync(
                new Uri($"/api/v1/sources/{stand.EntityId}/registry", UriKind.Relative), new { registryDefId = stand.RegistryId });
            Assert.Equal(HttpStatusCode.OK, bound.StatusCode);

            // Без Registry.EditData мапінг не заводиться (і нічого не записується).
            var denied = await MapAsync(managerOnly, stand.EntityId, stand.OwnFieldId).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            await using (var db = new EcrDbContext(Options()))
            {
                Assert.False(await db.EntityFieldMaps.AnyAsync(m => m.SourceEntityId == stand.EntityId).ConfigureAwait(true));
            }

            // Контроль: з правом на дані довідника — заводиться.
            var created = await MapAsync(editor, stand.EntityId, stand.OwnFieldId).ConfigureAwait(true);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            var mapId = (await JsonAsync(created).ConfigureAwait(true)).GetProperty("id").GetInt32();
            var mapUri = new Uri($"/api/v1/entity-field-maps/{mapId}", UriKind.Relative);

            // Той самий Manage-only не керує чужим мапінгом на довідник.
            foreach (var action in new[] { "pause", "resume" })
            {
                var response = await managerOnly.PostAsync(
                    new Uri($"/api/v1/entity-field-maps/{mapId}/{action}", UriKind.Relative), content: null);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            Assert.Equal(HttpStatusCode.Forbidden, (await managerOnly.DeleteAsync(mapUri)).StatusCode);
            await using (var db = new EcrDbContext(Options()))
            {
                var map = await db.EntityFieldMaps.AsNoTracking().SingleAsync(m => m.Id == mapId).ConfigureAwait(true);
                Assert.True(map.IsActive);
            }

            // Контроль: власник права призупиняє й видаляє.
            Assert.Equal(
                HttpStatusCode.OK,
                (await editor.PostAsync(new Uri($"/api/v1/entity-field-maps/{mapId}/pause", UriKind.Relative), content: null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await editor.DeleteAsync(mapUri)).StatusCode);
        }
        finally
        {
            await DeactivateAsync(dataSourceId).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-12.10")]
    public async Task ФВ_12_10_заведення_сутності_прив_язка_і_мапінг_лишають_журнал_зі_старим_і_новим_станом()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage, "Registry.EditData").ConfigureAwait(true);
        var dataSourceId = await DataSourceAsync().ConfigureAwait(true);
        var stand = await StandAsync(dataSourceId).ConfigureAwait(true);
        var code = $"Aud{Guid.NewGuid():N}"[..14];

        try
        {
            var created = await client.PostAsJsonAsync(Sources, new { dataSourceId, code, displayName = "Audit" });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var entityId = (await JsonAsync(created).ConfigureAwait(true)).GetProperty("id").GetInt32();

            var entityJournal = await StructureChangeProbe.ReadAsync(sql.ConnectionString, "ext.SourceEntity", entityId)
                .ConfigureAwait(true);
            var createRow = Assert.Single(entityJournal);
            Assert.Equal("CreateSourceEntity", createRow.Operation);
            Assert.Null(createRow.OldJson);
            Assert.Contains(code, createRow.NewJson, StringComparison.Ordinal);

            // Прив'язка: у старому стані довідника немає, у новому — є.
            var bind = await client.PutAsJsonAsync(
                new Uri($"/api/v1/sources/{entityId}/registry", UriKind.Relative), new { registryDefId = stand.RegistryId });
            Assert.Equal(HttpStatusCode.OK, bind.StatusCode);

            var afterBind = await StructureChangeProbe.ReadAsync(sql.ConnectionString, "ext.SourceEntity", entityId)
                .ConfigureAwait(true);
            var bindRow = Assert.Single(afterBind, r => r.Operation == "BindSourceEntityRegistry");
            Assert.Contains("\"registryDefId\":null", bindRow.OldJson, StringComparison.Ordinal);
            Assert.Contains($"\"registryDefId\":{stand.RegistryId}", bindRow.NewJson, StringComparison.Ordinal);

            // Повторна прив'язка до того самого довідника не змінює нічого — і не шумить у журналі.
            var again = await client.PutAsJsonAsync(
                new Uri($"/api/v1/sources/{entityId}/registry", UriKind.Relative), new { registryDefId = stand.RegistryId });
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Single(
                await StructureChangeProbe.ReadAsync(sql.ConnectionString, "ext.SourceEntity", entityId).ConfigureAwait(true),
                r => r.Operation == "BindSourceEntityRegistry");

            // Мапінг: створення пише новий стан (поле й ціль).
            var mapped = await client.PostAsJsonAsync(
                new Uri("/api/v1/entity-field-maps", UriKind.Relative),
                new
                {
                    sourceEntityId = entityId,
                    sourceField = "AuditField",
                    targetKind = "RegistryField",
                    targetRegistryFieldDefId = stand.OwnFieldId,
                });
            Assert.True(mapped.StatusCode == HttpStatusCode.OK, await mapped.Content.ReadAsStringAsync().ConfigureAwait(true));
            var mapId = (await JsonAsync(mapped).ConfigureAwait(true)).GetProperty("id").GetInt32();

            var mapRow = Assert.Single(
                await StructureChangeProbe.ReadAsync(sql.ConnectionString, "ext.EntityFieldMap", mapId).ConfigureAwait(true));
            Assert.Equal("CreateEntityFieldMap", mapRow.Operation);
            Assert.Null(mapRow.OldJson);
            Assert.Contains("AuditField", mapRow.NewJson, StringComparison.Ordinal);
            Assert.Contains($"\"targetRegistryFieldDefId\":{stand.OwnFieldId}", mapRow.NewJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeactivateAsync(dataSourceId).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// AN-34 L4-01: другу сутність того самого з'єднання не можна прив'язати до довідника, який тримає
    /// перша, - <c>409 ECR-INT-0409 registryAlreadyBound</c> (а не 422 чи 500); після відв'язки першої
    /// - можна. Інше з'єднання тримає той самий довідник незалежно.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "AN-34-L4-01")]
    public async Task AN34_L4_01_довідник_тримає_одна_сутність_з_єднання_друга_дає_409_а_після_відв_язки_першої_проходить()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Manage, "Registry.EditData").ConfigureAwait(true);
        var dataSourceId = await DataSourceAsync().ConfigureAwait(true);
        var otherDataSourceId = await DataSourceAsync().ConfigureAwait(true);
        var stand = await StandAsync(dataSourceId).ConfigureAwait(true);
        var otherStand = await StandAsync(otherDataSourceId).ConfigureAwait(true);
        var registryBody = new { registryDefId = stand.RegistryId };
        Uri RegistryOf(int entityId) => new($"/api/v1/sources/{entityId}/registry", UriKind.Relative);

        try
        {
            var created = await client.PostAsJsonAsync(Sources, new { dataSourceId, code = $"Second{Guid.NewGuid():N}"[..14] });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var secondId = (await JsonAsync(created).ConfigureAwait(true)).GetProperty("id").GetInt32();

            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(RegistryOf(stand.EntityId), registryBody)).StatusCode);

            var refused = await client.PutAsJsonAsync(RegistryOf(secondId), registryBody);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            var problem = await JsonAsync(refused).ConfigureAwait(true);
            Assert.Equal("ECR-INT-0409", problem.GetProperty("errorCode").GetString());
            Assert.Equal("err.ECR-INT-0409.registryAlreadyBound", problem.GetProperty("messageKey").GetString());
            Assert.Null(await BoundRegistryAsync(secondId).ConfigureAwait(true));

            // Повторна прив'язка власника до свого довідника - не конфлікт.
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(RegistryOf(stand.EntityId), registryBody)).StatusCode);

            // Інше з'єднання тримає той самий довідник незалежно.
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.PutAsJsonAsync(RegistryOf(otherStand.EntityId), registryBody)).StatusCode);

            // Відв'язка першої звільняє довідник другій.
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.PutAsJsonAsync(RegistryOf(stand.EntityId), new { registryDefId = (int?)null })).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync(RegistryOf(secondId), registryBody)).StatusCode);
            Assert.Equal(stand.RegistryId, await BoundRegistryAsync(secondId).ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(dataSourceId).ConfigureAwait(true);
            await DeactivateAsync(otherDataSourceId).ConfigureAwait(true);
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
