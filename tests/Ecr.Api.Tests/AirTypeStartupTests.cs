// tests/Ecr.Api.Tests/AirTypeStartupTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Registries.Nae;
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
/// ✎ AIR-ON-AIR (coordinator 02.11: «No runtime provision step for users»): the AIR type's sources tables and the Area registry are
/// applied on START (<c>AirTypeStartupStep</c>), idempotently; an open draft of the type is skipped; a missing Region registry makes
/// the REGION column Text instead of a refusal; the AREA column defaults to the AIR_AREA registry (Offshore / Onshore only).
/// </summary>
/// <remarks>
/// ⚠ Own database: the scenario needs an AIR type WITHOUT the sources tables and WITHOUT LAND_REGION, which the shared database
/// of the collection never is once the import tests ran. In the collection anyway: the API factory passes the connection string
/// through process-wide environment variables.
/// </remarks>
[Collection("SqlServer")]
public sealed class AirTypeStartupTests
{
    private const string Password = "Air-Startup-2026!";

    private static readonly string[] Admin =
    [
        "Registry.View", "Registry.EditDefinition", "Registry.EditData", "Registry.Publish", "Permit.EditType", "Permit.PublishType",
    ];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Start_applies_the_AIR_tables_idempotently_skips_an_open_draft_and_falls_back_to_Text_without_the_Region_registry()
    {
        var database = SqlServerFixture.WithOwnDatabase("_airstart");
        await database.InitializeAsync();
        try
        {
            // 1. A publisher, the NAE tree and the substance registry; NO LAND_REGION. AIR as on live: published with its own table.
            using (var app = new EcrApiFactory(database))
            {
                using var admin = await SignedInAsync(app, database);
                foreach (var code in new[] { NaeTreeSchema.RegistryCode, AirLimitsSchema.DefaultSubstanceRegistry })
                {
                    await ReadAsync(app, await admin.PostAsJsonAsync(
                        new Uri("/api/v1/registries", UriKind.Relative),
                        new { code, nameL10n = new Dictionary<string, string> { ["en"] = code }, isTemporal = false }));
                }

                var name = new Dictionary<string, string> { ["en"] = "Air emission permit" };
                var created = await ReadAsync(app, await admin.PostAsJsonAsync(
                    new Uri("/api/v1/permit-types", UriKind.Relative), new { code = "AIR", nameL10n = name, category = "Air" }));
                var draft = await ReadAsync(app, await admin.PutAsJsonAsync(
                    new Uri("/api/v1/permit-types/AIR", UriKind.Relative),
                    new { rowVersion = created.GetProperty("rowVersion").GetString(), nameL10n = name, category = "Air", tables = new[] { LimitsTable() }, rules = Array.Empty<object>() }));
                await ReadAsync(app, await admin.PostAsJsonAsync(
                    new Uri("/api/v1/permit-types/AIR/publish", UriKind.Relative),
                    new { reason = "Land-like AIR", rowVersion = draft.GetProperty("rowVersion").GetString() }));

                // Someone opens a new version of AIR and leaves it open.
                await ReadAsync(app, await admin.PostAsync(new Uri("/api/v1/permit-types/AIR/new-version", UriKind.Relative), null));
            }

            // 2. Start with the draft open: skipped, the type is untouched (no sources tables, the draft is still there).
            var afterDraft = await StartAndReadAirAsync(database);
            Assert.DoesNotContain(AirLimitsSchema.SourcesTable, Tables(afterDraft));
            Assert.True(afterDraft.GetProperty("hasUnpublishedChanges").GetBoolean(), afterDraft.ToString());

            // 3. The draft is published by its owner; the next start appends the sources tables as a new version.
            using (var app = new EcrApiFactory(database))
            {
                using var admin = await SignedInAsync(app, database);
                await ReadAsync(app, await admin.PostAsJsonAsync(
                    new Uri("/api/v1/permit-types/AIR/publish", UriKind.Relative),
                    new { reason = "draft done", rowVersion = afterDraft.GetProperty("rowVersion").GetString() }));
            }

            var provisioned = await StartAndReadAirAsync(database);
            Assert.Equal([AirLimitsSchema.SourcesTable, AirLimitsSchema.SubstancesTable, "EMISSION_LIMITS"], Tables(provisioned).Order(StringComparer.Ordinal));
            Assert.False(provisioned.GetProperty("hasUnpublishedChanges").GetBoolean());
            var columns = provisioned.GetProperty("tables").EnumerateArray()
                .Single(t => t.GetProperty("code").GetString() == AirLimitsSchema.SourcesTable)
                .GetProperty("columns").EnumerateArray().ToDictionary(c => c.GetProperty("code").GetString()!);

            // No LAND_REGION → REGION is Text (warning, not 422); AREA is the AIR_AREA registry, optional.
            Assert.Equal("String", columns["REGION"].GetProperty("dataType").GetString());
            Assert.Equal(("Lookup", "AIR_AREA", false), (columns["AREA"].GetProperty("dataType").GetString(), columns["AREA"].GetProperty("listRegistryCode").GetString(), columns["AREA"].GetProperty("required").GetBoolean()));

            var areas = await AreaEntriesAsync(database);
            Assert.Equal(["OFFSHORE|Offshore|Морской|Теңіздегі", "ONSHORE|Onshore|Наземный|Құрлықтағы"], areas);

            // 4. Twice in a row: the second start changes nothing.
            var again = await StartAndReadAirAsync(database);
            Assert.Equal(provisioned.GetProperty("version").GetInt32(), again.GetProperty("version").GetInt32());
            Assert.Equal(areas, await AreaEntriesAsync(database));
        }
        finally
        {
            await database.DisposeAsync();
            await DropAsync(database);
        }
    }

    private static object LimitsTable() => new
    {
        code = "EMISSION_LIMITS",
        nameL10n = new Dictionary<string, string> { ["en"] = "Emission limits" },
        order = 1,
        columns = new object[]
        {
            new
            {
                code = "LIMIT_NOTE", nameL10n = new Dictionary<string, string> { ["en"] = "Note" }, dataType = "String",
                unitId = (int?)null, required = true, key = true, group = (string?)null, precision = (int?)null, scale = (int?)null,
                listScope = (string?)null, listRegistryCode = (string?)null, siblingTableCode = (string?)null, choices = (string[]?)null, order = 1,
            },
        },
        rules = Array.Empty<object>(),
    };

    private static List<string> Tables(JsonElement type)
        => [.. type.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("code").GetString()!)];

    /// <summary>A fresh application start (the startup sequence runs) and the AIR type as the API reports it.</summary>
    private static async Task<JsonElement> StartAndReadAirAsync(SqlServerFixture database)
    {
        using var app = new EcrApiFactory(database);
        using var admin = await SignedInAsync(app, database);
        return await ReadAsync(app, await admin.GetAsync(new Uri("/api/v1/permit-types/AIR", UriKind.Relative)));
    }

    private static async Task<List<string>> AreaEntriesAsync(SqlServerFixture database)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(database.ConnectionString).Options);
        var def = await db.RegistryDefs.AsNoTracking().SingleAsync(d => d.Code == AirLimitsSchema.DefaultAreaRegistry);
        return [.. (await db.RegistryEntries.AsNoTracking().Where(e => e.RegistryDefId == def.Id && !e.IsDeleted).ToListAsync())
            .Select(e => $"{e.Code}|{e.DisplayL10n.Get("en")}|{e.DisplayL10n.Get("ru")}|{e.DisplayL10n.Get("kz")}")
            .Order(StringComparer.Ordinal)];
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, SqlServerFixture database)
    {
        var name = $"airs_{Guid.NewGuid():N}"[..20];
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(database.ConnectionString).Options))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Air startup test" }));
            db.Roles.Add(role);
            await db.SaveChangesAsync();
            foreach (var permission in Admin)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static async Task<JsonElement> ReadAsync(EcrApiFactory app, HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text} | {app.ErrorsText}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    private static async Task DropAsync(SqlServerFixture database)
    {
        var master = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"IF DB_ID(N'{database.DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{database.DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database.DatabaseName}]; END";
        await command.ExecuteNonQueryAsync();
    }
}
