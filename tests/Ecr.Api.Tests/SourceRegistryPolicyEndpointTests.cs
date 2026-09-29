// tests/Ecr.Api.Tests/SourceRegistryPolicyEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// <c>PUT /api/v1/sources/{id}/registry/policy</c> — політика синку довідника з AF
/// (<c>D-212</c>, PR-2) — і <c>MissingInSourceSince</c> у переліку зовнішніх ключів.
/// </summary>
/// <remarks>
/// ⛔ Доказ на HTTP, а не на обробнику: 403/404/422 розводить
/// <c>ExceptionHandlingMiddleware</c>, і лише тут видно, що клієнт отримує саме їх.
///
/// ⚠ Право на довідник — ГРАНТ <c>Write</c>, а не глобальне <c>Registry.EditData</c>:
/// вужчий шлях доводить, що перевіряється САМЕ прив'язаний довідник. Кожна
/// сутність наприкінці вимикається — база спільна.
/// </remarks>
[Collection("SqlServer")]
public sealed class SourceRegistryPolicyEndpointTests(SqlServerFixture sql)
{
    private const string Password = "Source-Registry-Policy-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-212")]
    public async Task Грант_Write_на_прив_язаний_довідник_дає_200_пише_політику_і_журнал()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await StandAsync(bound: true).ConfigureAwait(true);

        try
        {
            using var client = await SignedInAsync(app, stand.RegistryId).ConfigureAwait(true);

            var response = await client.PutAsJsonAsync(
                Uri(stand.EntityId),
                new { onMissingInSource = "Deactivate", validFromAttribute = " Start ", validToAttribute = "End", validToInclusive = true });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true)).RootElement;
            Assert.Equal("Deactivate", dto.GetProperty("onMissingInSource").GetString());
            Assert.Equal("Start", dto.GetProperty("validFromAttribute").GetString());
            Assert.True(dto.GetProperty("validToInclusive").GetBoolean());

            var row = await EntityAsync(stand.EntityId).ConfigureAwait(true);
            Assert.Equal(
                (RegistryMissingPolicy.Deactivate, "Start", "End", true),
                (row.OnMissingInSource, row.ValidFromAttribute, row.ValidToAttribute, row.ValidToInclusive));

            var audit = Assert.Single(await AuditAsync(stand.EntityId).ConfigureAwait(true));
            Assert.Contains("\"MarkOrphaned\"", audit.OldJson, StringComparison.Ordinal);
            Assert.Contains("\"Deactivate\"", audit.NewJson, StringComparison.Ordinal);
            Assert.Contains("\"End\"", audit.NewJson, StringComparison.Ordinal);
        }
        finally
        {
            await DeactivateAsync(stand.DataSourceId).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-212")]
    public async Task Без_права_на_прив_язаний_довідник_чи_без_Integration_Manage_403_і_нічого_не_пише()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await StandAsync(bound: true).ConfigureAwait(true);

        try
        {
            // Integration.Manage є, грант — на ІНШИЙ довідник.
            using var stranger = await SignedInAsync(app, stand.OtherRegistryId).ConfigureAwait(true);
            var denied = await stranger.PutAsJsonAsync(Uri(stand.EntityId), Body("Deactivate"));

            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            var body = await denied.Content.ReadAsStringAsync().ConfigureAwait(true);
            Assert.Equal("ECR-AUTH-0403", JsonDocument.Parse(body).RootElement.GetProperty("errorCode").GetString());
            Assert.Contains("Registry.EditData", body, StringComparison.Ordinal);

            // Право на дані довідника є (глобальне), Integration.Manage — ні.
            using var registryOnly = await SystemHealthControllerTests
                .SignedInAsync(sql, app, "Registry.EditData", "Registry.View").ConfigureAwait(true);
            var noManage = await registryOnly.PutAsJsonAsync(Uri(stand.EntityId), Body("Deactivate"));

            Assert.Equal(HttpStatusCode.Forbidden, noManage.StatusCode);
            Assert.Contains(
                "Integration.Manage", await noManage.Content.ReadAsStringAsync().ConfigureAwait(true), StringComparison.Ordinal);

            Assert.Equal(RegistryMissingPolicy.MarkOrphaned, (await EntityAsync(stand.EntityId).ConfigureAwait(true)).OnMissingInSource);
            Assert.Empty(await AuditAsync(stand.EntityId).ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(stand.DataSourceId).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-212")]
    public async Task Неіснуюча_чи_вимкнена_сутність_дає_404()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await StandAsync(bound: true).ConfigureAwait(true);

        try
        {
            using var client = await SignedInAsync(app, stand.RegistryId).ConfigureAwait(true);

            var missing = await client.PutAsJsonAsync(Uri(int.MaxValue), Body("Ignore"));
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("ECR-INT-0404", (await JsonAsync(missing).ConfigureAwait(true)).GetProperty("errorCode").GetString());

            // Вимкнена сутність невидима для налаштування так само, як для прив'язки.
            await DeactivateAsync(stand.DataSourceId).ConfigureAwait(true);
            var inactive = await client.PutAsJsonAsync(Uri(stand.EntityId), Body("Ignore"));
            Assert.Equal(HttpStatusCode.NotFound, inactive.StatusCode);
            Assert.Equal(RegistryMissingPolicy.MarkOrphaned, (await EntityAsync(stand.EntityId).ConfigureAwait(true)).OnMissingInSource);
        }
        finally
        {
            await DeactivateAsync(stand.DataSourceId).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-212")]
    public async Task Невалідне_тіло_чи_неприв_язана_сутність_дають_422_і_нічого_не_пишуть()
    {
        using var app = new EcrApiFactory(sql);
        var bound = await StandAsync(bound: true).ConfigureAwait(true);
        var unbound = await StandAsync(bound: false).ConfigureAwait(true);

        try
        {
            using var client = await SignedInAsync(app, bound.RegistryId).ConfigureAwait(true);

            // Включна межа без атрибута кінця.
            var inclusive = await client.PutAsJsonAsync(
                Uri(bound.EntityId),
                new { onMissingInSource = "Deactivate", validFromAttribute = "Start", validToAttribute = (string?)null, validToInclusive = true });
            await AssertProblemAsync(inclusive, "err.ECR-REQ-0422.registrySyncPolicyInvalid").ConfigureAwait(true);

            // Ім'я атрибута довше за колонку.
            var tooLong = await client.PutAsJsonAsync(
                Uri(bound.EntityId),
                new { onMissingInSource = "Deactivate", validFromAttribute = new string('a', 201), validToAttribute = (string?)null, validToInclusive = false });
            await AssertProblemAsync(tooLong, "err.ECR-REQ-0422.registrySyncPolicyInvalid").ConfigureAwait(true);

            // Невідоме значення переліку — відмова ще на прив'язці моделі.
            var bogus = await client.PutAsJsonAsync(Uri(bound.EntityId), Body("Bogus"));
            await AssertProblemAsync(bogus, "err.ECR-REQ-0422.malformedRequest").ConfigureAwait(true);

            // Сутність без довідника: політиці немає до чого застосуватися.
            var notBound = await client.PutAsJsonAsync(Uri(unbound.EntityId), Body("Deactivate"));
            await AssertProblemAsync(notBound, "err.ECR-REQ-0422.registrySyncPolicyNotBound").ConfigureAwait(true);

            Assert.Equal(RegistryMissingPolicy.MarkOrphaned, (await EntityAsync(bound.EntityId).ConfigureAwait(true)).OnMissingInSource);
            Assert.Empty(await AuditAsync(bound.EntityId).ConfigureAwait(true));
            Assert.Empty(await AuditAsync(unbound.EntityId).ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(bound.DataSourceId).ConfigureAwait(true);
            await DeactivateAsync(unbound.DataSourceId).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-212")]
    public async Task Перелік_зовнішніх_ключів_віддає_MissingInSourceSince()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await StandAsync(bound: true).ConfigureAwait(true);
        var since = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);

        try
        {
            string registryCode;
            await using (var db = Context())
            {
                registryCode = await db.RegistryDefs.Where(r => r.Id == stand.RegistryId).Select(r => r.Code)
                    .SingleAsync().ConfigureAwait(true);
                var entry = new RegistryEntry(stand.RegistryId, EcrCode.Create("FL01"), Name("FL01"));
                db.RegistryEntries.Add(entry);
                await db.SaveChangesAsync().ConfigureAwait(true);

                var missing = new RegistryExternalKey(entry.Id, stand.DataSourceId, "gone-" + Guid.NewGuid().ToString("N"));
                missing.MarkMissing(since);
                var present = new RegistryExternalKey(entry.Id, stand.DataSourceId, "here-" + Guid.NewGuid().ToString("N"));
                db.RegistryExternalKeys.AddRange(missing, present);
                await db.SaveChangesAsync().ConfigureAwait(true);
            }

            using var client = await SystemHealthControllerTests.SignedInAsync(sql, app, "Registry.View").ConfigureAwait(true);
            var list = await JsonAsync(
                await client.GetAsync(new Uri($"/api/v1/registries/{registryCode}/external-keys", UriKind.Relative))
                    .ConfigureAwait(true)).ConfigureAwait(true);

            var items = list.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);

            var gone = items.Single(i => i.GetProperty("externalId").GetString()!.StartsWith("gone-", StringComparison.Ordinal));
            Assert.Equal(since, gone.GetProperty("missingInSourceSince").GetDateTime().ToUniversalTime());

            var here = items.Single(i => i.GetProperty("externalId").GetString()!.StartsWith("here-", StringComparison.Ordinal));
            Assert.Equal(JsonValueKind.Null, here.GetProperty("missingInSourceSince").ValueKind);
        }
        finally
        {
            await DeactivateAsync(stand.DataSourceId).ConfigureAwait(true);
        }
    }

    private sealed record Stand(int DataSourceId, int EntityId, int RegistryId, int OtherRegistryId);

    private sealed record AuditRow(string? OldJson, string? NewJson);

    private static Uri Uri(int entityId) => new($"/api/v1/sources/{entityId}/registry/policy", UriKind.Relative);

    private static object Body(string policy)
        => new { onMissingInSource = policy, validFromAttribute = (string?)null, validToAttribute = (string?)null, validToInclusive = false };

    private static async Task AssertProblemAsync(HttpResponseMessage response, string messageKey)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await JsonAsync(response).ConfigureAwait(false);
        Assert.Equal("ECR-REQ-0422", problem.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, problem.GetProperty("messageKey").GetString());
    }

    private async Task<Stand> StandAsync(bool bound)
    {
        await using var db = Context();
        var tag = $"{Guid.NewGuid():N}"[..8];

        var source = new DataSource(
            EcrCode.Create($"Pol{tag}"), Name("D-212"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(source);
        var target = new RegistryDef(EcrCode.Create($"RPT{tag}"), Name("target"), isTemporal: false);
        var other = new RegistryDef(EcrCode.Create($"RPO{tag}"), Name("other"), isTemporal: false);
        db.RegistryDefs.AddRange(target, other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var entity = new SourceEntity(source.Id, $"Ent{tag}", RegistrySourceKind.External);
        if (bound)
        {
            entity.BindRegistry(target.Id);
        }

        db.SourceEntities.Add(entity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(source.Id, entity.Id, target.Id, other.Id);
    }

    /// <summary>
    /// Користувач з <c>Integration.Manage</c> БЕЗ глобального <c>Registry.EditData</c>
    /// і з грантом <c>Write</c> лише на <paramref name="grantedRegistryId"/>.
    /// </summary>
    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, int grantedRegistryId)
    {
        var name = $"pol_{Guid.NewGuid():N}"[..20];

        await using (var db = Context())
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Source policy test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync().ConfigureAwait(false);

            db.RolePermissions.Add(new RolePermission(role.Id, Ecr.Application.Sources.CreateSourceEntityHandler.Permission));
            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            db.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Registry, grantedRegistryId, GrantLevel.Write));
            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password })
            .ConfigureAwait(false);

        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");

        return client;
    }

    private async Task<SourceEntity> EntityAsync(int entityId)
    {
        await using var db = Context();
        return await db.SourceEntities.AsNoTracking().SingleAsync(e => e.Id == entityId).ConfigureAwait(false);
    }

    private async Task<List<AuditRow>> AuditAsync(int entityId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT OldJson, NewJson FROM aud.StructureChange "
            + "WHERE EntityType = N'ext.SourceEntity' AND EntityId = @id AND Operation = N'SetRegistrySyncPolicy';";
        command.Parameters.AddWithValue("@id", entityId);

        var rows = new List<AuditRow>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new AuditRow(
                reader.IsDBNull(0) ? null : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    /// <summary>Вимикає сутності з'єднання — спільна база не має лишитися з «прогалиною».</summary>
    private async Task DeactivateAsync(int dataSourceId)
    {
        await using var db = Context();

        var entities = await db.SourceEntities.Where(e => e.DataSourceId == dataSourceId).ToListAsync()
            .ConfigureAwait(false);
        entities.ForEach(e => e.Deactivate());
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });
}
