// tests/Ecr.Api.Tests/SourceBindRegistryPermissionTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
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
/// Прив'язка сутності збору до довідника вимагає, окрім <c>Integration.Manage</c>,
/// права редагувати дані САМЕ ЦЬОГО довідника — <c>Registry.EditData</c> або
/// ресурсний грант <c>Write</c> на <c>RegistryDefId</c> (<c>D-202</c>, доповнення
/// 2026-09-29: судження розробки, на підтвердження людиною).
/// </summary>
/// <remarks>
/// ⛔ Чому: після прив'язки синк пише в дані довідника від імені
/// <c>svc-integration</c>. Без цієї перевірки власник лише <c>Integration.Manage</c>
/// міг спрямувати зовнішнє джерело в довідник, редагувати який сам не має права.
///
/// ⚠ Грант, а не глобальне право: вужчий шлях доводить, що перевіряється саме
/// ЦІЛЬОВИЙ довідник (глобальне <c>Registry.EditData</c> пропустило б і будь-який
/// інший). Кожна сутність наприкінці вимикається — база спільна.
/// </remarks>
[Collection("SqlServer")]
public sealed class SourceBindRegistryPermissionTests(SqlServerFixture sql)
{
    private const string Password = "Source-Bind-Registry-2026!";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Integration_Manage_без_права_на_довідник_дає_403_і_не_прив_язує()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await StandAsync().ConfigureAwait(true);

        try
        {
            // Грант Write — на ІНШИЙ довідник: права на цільовий немає.
            using var client = await SignedInAsync(app, stand.OtherRegistryId).ConfigureAwait(true);

            var response = await client.PutAsJsonAsync(Uri(stand.EntityId), new { registryDefId = stand.RegistryId });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            var problem = JsonDocument.Parse(body).RootElement;
            Assert.Equal("ECR-AUTH-0403", problem.GetProperty("errorCode").GetString());
            Assert.Contains("Registry.EditData", body, StringComparison.Ordinal);

            Assert.Null(await BoundRegistryAsync(stand.EntityId).ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(stand.DataSourceId).ConfigureAwait(true);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.11")]
    public async Task Грант_Write_на_довідник_дозволяє_прив_язку_а_відв_язку_лише_з_правом_на_поточний()
    {
        using var app = new EcrApiFactory(sql);
        var stand = await StandAsync().ConfigureAwait(true);

        try
        {
            using var granted = await SignedInAsync(app, stand.RegistryId).ConfigureAwait(true);

            var bound = await granted.PutAsJsonAsync(Uri(stand.EntityId), new { registryDefId = stand.RegistryId });
            Assert.Equal(HttpStatusCode.OK, bound.StatusCode);
            Assert.Equal(stand.RegistryId, await BoundRegistryAsync(stand.EntityId).ConfigureAwait(true));

            // Відв'язка тим, хто має право лише на ІНШИЙ довідник, — 403, прив'язка стоїть.
            using var stranger = await SignedInAsync(app, stand.OtherRegistryId).ConfigureAwait(true);

            var unbindDenied = await stranger.PutAsJsonAsync(Uri(stand.EntityId), new { registryDefId = (int?)null });
            Assert.Equal(HttpStatusCode.Forbidden, unbindDenied.StatusCode);

            var moveDenied = await stranger.PutAsJsonAsync(
                Uri(stand.EntityId), new { registryDefId = stand.OtherRegistryId });
            Assert.Equal(HttpStatusCode.Forbidden, moveDenied.StatusCode);
            Assert.Equal(stand.RegistryId, await BoundRegistryAsync(stand.EntityId).ConfigureAwait(true));

            var unbound = await granted.PutAsJsonAsync(Uri(stand.EntityId), new { registryDefId = (int?)null });
            Assert.Equal(HttpStatusCode.OK, unbound.StatusCode);
            Assert.Null(await BoundRegistryAsync(stand.EntityId).ConfigureAwait(true));
        }
        finally
        {
            await DeactivateAsync(stand.DataSourceId).ConfigureAwait(true);
        }
    }

    private sealed record Stand(int DataSourceId, int EntityId, int RegistryId, int OtherRegistryId);

    private static Uri Uri(int entityId) => new($"/api/v1/sources/{entityId}/registry", UriKind.Relative);

    private async Task<Stand> StandAsync()
    {
        await using var db = new EcrDbContext(Options());
        var tag = $"{Guid.NewGuid():N}"[..8];

        var source = new DataSource(
            EcrCode.Create($"Src{tag}"), Name("D-202"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        db.DataSources.Add(source);
        var target = new RegistryDef(EcrCode.Create($"RBT{tag}"), Name("target"), isTemporal: false);
        var other = new RegistryDef(EcrCode.Create($"RBO{tag}"), Name("other"), isTemporal: false);
        db.RegistryDefs.AddRange(target, other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var entity = new SourceEntity(source.Id, $"Ent{tag}", RegistrySourceKind.External);
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
        var name = $"bind_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("Source bind test"));
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

    private async Task<int?> BoundRegistryAsync(int entityId)
    {
        await using var db = new EcrDbContext(Options());

        return await db.SourceEntities.AsNoTracking()
            .Where(e => e.Id == entityId).Select(e => e.RegistryDefId).SingleAsync().ConfigureAwait(false);
    }

    /// <summary>Вимикає сутності з'єднання — спільна база не має лишитися з «прогалиною».</summary>
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
}
