// tests/Ecr.Api.Tests/AirTypeStartupTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
            // 0. ✎ AIRSYS02: an NAE_TREE that already exists BEFORE the first start (here a flat, empty one — not a tree) is left
            //    exactly as it is by every start below; AIR provisioning still goes ahead as before.
            var preExisting = await AddFlatRegistryAsync(database, NaeTreeSchema.RegistryCode);

            // 1. A publisher and the substance registry; NO LAND_REGION. AIR as on live: published with its own table.
            using (var app = new EcrApiFactory(database))
            {
                using var admin = await SignedInAsync(app, database);
                foreach (var code in new[] { AirLimitsSchema.DefaultSubstanceRegistry })
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

            // ✎ AIRSYS01: the new version is the SYSTEM's — no user on the type row, 0 «system» in the audit trail, «system» in
            // the type list; no real user id for this publication.
            var (publishedBy, updatedBy, publishAudit) = await AuthorsAsync(database);
            Assert.Null(publishedBy);
            Assert.Null(updatedBy);
            Assert.Equal(0, publishAudit[^1]);
            using (var app = new EcrApiFactory(database))
            {
                using var admin = await SignedInAsync(app, database);
                var list = await ReadAsync(app, await admin.GetAsync(new Uri("/api/v1/permit-types?limit=50", UriKind.Relative)));
                var air = list.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("code").GetString() == "AIR");
                Assert.Equal("system", air.GetProperty("updatedByDisplayName").GetString());

                // The HTTP fallback keeps both permissions.
                using var reader = await SignedInAsync(app, database, ["Registry.View"]);
                Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync(new Uri("/api/v1/air-limits/provision", UriKind.Relative), null)).StatusCode);
            }

            // 4. Twice in a row: the second start changes nothing (the version is appended once, the Land table stays).
            var again = await StartAndReadAirAsync(database);
            Assert.Equal(provisioned.GetProperty("version").GetInt32(), again.GetProperty("version").GetInt32());
            Assert.Equal(afterDraft.GetProperty("version").GetInt32() + 2, again.GetProperty("version").GetInt32());
            Assert.Contains("EMISSION_LIMITS", Tables(again));
            Assert.Equal(areas, await AreaEntriesAsync(database));
            Assert.Equal(publishAudit.Count, (await AuthorsAsync(database)).PublishAudit.Count);

            // The pre-existing NAE_TREE: untouched by four starts.
            Assert.Equal(preExisting, await NaeStateAsync(database));
        }
        finally
        {
            await database.DisposeAsync();
            await DropAsync(database);
        }
    }

    /// <summary>
    /// ✎ AIRSYS02 (live-copy check 02.11): live has NO NAE tree, so the AIR prerequisite refused on every start and the
    /// «Import parameters…» UI was dead. Now the start creates NAE_TREE EMPTY as the system (definition, fields, business key, five
    /// levels, no entries, author 0 «system»), and AIR provisioning goes ahead in the same start. A second start changes nothing.
    /// </summary>
    /// <remarks>
    /// Mutation proof (local, not committed): make <c>NaeTreeProvisioner.EnsureAsSystemAsync</c> re-save the levels of an existing
    /// registry instead of returning → the second start writes a history row and the test fails on the unchanged state.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Start_without_NAE_TREE_creates_it_empty_as_system_then_provisions_AIR_and_a_second_start_changes_nothing()
    {
        var database = SqlServerFixture.WithOwnDatabase("_airnae");
        await database.InitializeAsync();
        try
        {
            // Live-like: the substance registry exists, NAE_TREE does not, AIR not provisioned.
            _ = await AddFlatRegistryAsync(database, AirLimitsSchema.DefaultSubstanceRegistry);

            var first = await StartAndReadAirAsync(database);
            Assert.Contains(AirLimitsSchema.SourcesTable, Tables(first));
            Assert.Contains(AirLimitsSchema.SubstancesTable, Tables(first));

            var created = await NaeStateAsync(database);
            Assert.True(created.IsHierarchical, created.ToString());
            Assert.Equal(["REGION", "AREA", "SITE", "EQUIP", "SRC"], created.Levels);
            Assert.Equal(0, created.Entries);
            Assert.Equal(
                new[] { NaeTreeSchema.NaeCodeField, NaeTreeSchema.RegionField, NaeTreeSchema.AreaField, NaeTreeSchema.AfPathField, NaeTreeSchema.SourceTypeField, NaeTreeSchema.PointNameField }
                    .Order(StringComparer.Ordinal).ToArray(),
                created.Fields.ToArray());
            Assert.Equal(1, created.BusinessKeys);
            Assert.Equal(0, created.UpdatedBy);
            Assert.NotEmpty(created.HistoryAuthors);
            Assert.All(created.HistoryAuthors, a => Assert.Equal(0, a));

            // Second start: nothing new — no definition version, no history row, no AIR version.
            var second = await StartAndReadAirAsync(database);
            Assert.Equal(created, await NaeStateAsync(database));
            Assert.Equal(first.GetProperty("version").GetInt32(), second.GetProperty("version").GetInt32());

            // The HTTP paths keep their permission checks: a reader cannot create, describe or re-level a registry.
            using var app = new EcrApiFactory(database);
            using var reader = await SignedInAsync(app, database, ["Registry.View"]);
            var code = NaeTreeSchema.RegistryCode;
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(
                new Uri("/api/v1/registries", UriKind.Relative),
                new { code = "AIRNAE_X", nameL10n = new Dictionary<string, string> { ["en"] = "x" }, isTemporal = false })).StatusCode);
            using var definition = new HttpRequestMessage(HttpMethod.Put, new Uri($"/api/v1/registries/{code}/definition", UriKind.Relative))
            {
                Content = JsonContent.Create(new { fields = Array.Empty<object>(), rules = Array.Empty<object>(), reason = "x" }),
            };
            definition.Headers.TryAddWithoutValidation("If-Match", created.DefinitionVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.SendAsync(definition)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(
                new Uri($"/api/v1/registries/{code}/hierarchy", UriKind.Relative),
                new { isHierarchical = false, levels = Array.Empty<object>() })).StatusCode);
            Assert.Equal(created, await NaeStateAsync(database));

            // The AF import fills the EMPTY system-created tree (no second provisioning, user = author of the nodes).
            using var withAf = new EcrApiFactory(database) { ExtraServices = s => s.AddSingleton<INaeAfReader>(new OneSourceAf()) };
            using var importer = await SignedInAsync(withAf, database);
            var import = await ReadAsync(withAf, await importer.PostAsJsonAsync(
                new Uri("/api/v1/nae-tree/af-import", UriKind.Relative),
                new { dataSourceId = 1, rootPath = OneSourceAf.Root, registryCode = code, dryRun = false }));
            Assert.True(import.GetProperty("applied").GetBoolean(), import.ToString());
            Assert.False(import.GetProperty("registryProvisioned").GetBoolean());
            var tree = await ReadAsync(withAf, await importer.GetAsync(new Uri($"/api/v1/registries/{code}/tree", UriKind.Relative)));
            Assert.Single(tree.GetProperty("nodes").EnumerateArray(), n => n.GetProperty("levelCode").GetString() == "SRC");
        }
        finally
        {
            await database.DisposeAsync();
            await DropAsync(database);
        }
    }

    /// <summary>AF with one NAE source under Atyrau / Offshore / Island_A.</summary>
    private sealed class OneSourceAf : INaeAfReader
    {
        public const string Root = @"\\AF\ECR\ECR_01_Air Equipment";

        public Task<NaeAfSnapshot> ReadAsync(int dataSourceId, string rootPath, CancellationToken ct)
            => Task.FromResult(new NaeAfSnapshot(
            [
                new NaeAfElement($@"{Root}\Atyrau_Air", "Atyrau_Air", null, null, null, null, null),
                new NaeAfElement($@"{Root}\Atyrau_Air\Offshore\Island_A", "Island_A", null, null, null, null, null),
                new NaeAfElement($@"{Root}\Atyrau_Air\Offshore\Island_A\9001", "9001", "Start NAE", null, null, "Atyrau", "Offshore"),
            ], true));
    }

    /// <summary>A flat, empty registry inserted before any start (no HTTP, no author).</summary>
    private static async Task<NaeState?> AddFlatRegistryAsync(SqlServerFixture database, string code)
    {
        await using (var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(database.ConnectionString).Options))
        {
            db.RegistryDefs.Add(new Ecr.Domain.Entities.Configuration.RegistryDef(
                EcrCode.Create(code), new LocalizedText(new Dictionary<string, string> { ["en"] = code }), isTemporal: false));
            await db.SaveChangesAsync();
        }

        return code == NaeTreeSchema.RegistryCode ? await NaeStateAsync(database) : null;
    }

    /// <summary>Everything a start could change on NAE_TREE: shape, version, author, entries and its history rows.</summary>
    private sealed record NaeState(
        bool IsHierarchical, int DefinitionVersion, int? UpdatedBy, int Entries, int BusinessKeys,
        IReadOnlyList<string> Fields, IReadOnlyList<string> Levels, IReadOnlyList<int> HistoryAuthors)
    {
        public bool Equals(NaeState? other)
            => other is not null && (IsHierarchical, DefinitionVersion, UpdatedBy, Entries, BusinessKeys) == (other.IsHierarchical, other.DefinitionVersion, other.UpdatedBy, other.Entries, other.BusinessKeys)
               && Fields.SequenceEqual(other.Fields) && Levels.SequenceEqual(other.Levels) && HistoryAuthors.SequenceEqual(other.HistoryAuthors);

        public override int GetHashCode() => HashCode.Combine(IsHierarchical, DefinitionVersion, UpdatedBy, Entries);

        public override string ToString()
            => $"hier={IsHierarchical} v={DefinitionVersion} by={UpdatedBy} entries={Entries} keys={BusinessKeys} fields=[{string.Join(",", Fields)}] levels=[{string.Join(",", Levels)}] history=[{string.Join(",", HistoryAuthors)}]";
    }

    private static async Task<NaeState> NaeStateAsync(SqlServerFixture database)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(database.ConnectionString).Options);
        var def = await db.RegistryDefs.AsNoTracking().Include(d => d.Fields).SingleAsync(d => d.Code == NaeTreeSchema.RegistryCode);
        var levels = await db.RegistryLevelDefs.AsNoTracking().Where(l => l.RegistryDefId == def.Id).OrderBy(l => l.Ordinal).Select(l => l.Code).ToListAsync();
        var entries = await db.RegistryEntries.AsNoTracking().CountAsync(e => e.RegistryDefId == def.Id);
        var keys = await db.RegistryKeyDefs.AsNoTracking().CountAsync(k => k.RegistryDefId == def.Id && k.Code == NaeTreeSchema.BusinessKeyCode && k.IsActive);
        var history = await db.Database
            .SqlQuery<int>($"SELECT ChangedByUserId AS Value FROM aud.StructureChange WHERE EntityType = N'cfg.RegistryDef' AND EntityId = {def.Id} ORDER BY ChangedAt, Id")
            .ToListAsync();
        return new NaeState(
            def.IsHierarchical, def.DefinitionVersion, def.DefinitionUpdatedByUserId, entries, keys,
            [.. def.Fields.Select(f => f.Code).Order(StringComparer.Ordinal)], levels, history);
    }

    /// <summary>✎ AIRSYS01: the system path is reachable from the startup step only — no controller takes it.</summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void No_controller_reaches_the_system_provisioner()
    {
        var controllers = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t) && !t.IsAbstract);
        var offenders = controllers
            .Where(c => c.GetConstructors().SelectMany(k => k.GetParameters())
                .Any(p => p.ParameterType == typeof(AirTypeSystemProvisioner)))
            .Select(c => c.Name)
            .ToList();

        Assert.NotEmpty(controllers);
        Assert.Empty(offenders);

        // ✎ AIRSYS02: the author-taking (permission-free) methods the system path uses stay invisible to Ecr.Api — a public one
        // would let a controller call it with a handler it legitimately takes.
        var systemMethods = new (Type Type, string Method)[]
        {
            (typeof(NaeTreeProvisioner), "EnsureAsSystemAsync"),
            (typeof(Ecr.Application.Registries.CreateRegistryHandler), "ApplyAsync"),
            (typeof(Ecr.Application.Registries.SaveRegistryDefinitionHandler), "SaveForAuthorAsync"),
            (typeof(Ecr.Application.Registries.Hierarchy.SaveRegistryHierarchyHandler), "SaveForAuthorAsync"),
        };
        foreach (var (type, method) in systemMethods)
        {
            var found = type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Where(m => m.Name == method).ToList();
            Assert.True(found.Count > 0, $"{type.Name}.{method} is gone — update the guard.");
            Assert.All(found, m => Assert.True(m.IsAssembly, $"{type.Name}.{method} must stay internal."));
        }
    }

    /// <summary>Authors of the AIR type row and the audit authors of its publications (oldest first).</summary>
    private static async Task<(int? PublishedBy, int? UpdatedBy, List<int> PublishAudit)> AuthorsAsync(SqlServerFixture database)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(database.ConnectionString).Options);
        var type = await db.PermitTypeDefs.AsNoTracking().SingleAsync(t => t.Code == "AIR");
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT ChangedByUserId FROM aud.StructureChange WHERE EntityType = N'cfg.PermitTypeDef' AND EntityId = @id AND Operation = N'PublishPermitType' ORDER BY ChangedAt, Id";
        command.Parameters.AddWithValue("@id", type.Id);
        var ids = new List<int>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                ids.Add(Convert.ToInt32(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return (type.PublishedByUserId, type.UpdatedByUserId, ids);
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

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, SqlServerFixture database, string[]? permissions = null)
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
            foreach (var permission in permissions ?? Admin)
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
