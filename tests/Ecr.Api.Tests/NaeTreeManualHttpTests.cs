// tests/Ecr.Api.Tests/NaeTreeManualHttpTests.cs
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Ecr.Application.Ports;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
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
/// NAE tree without AF (human 02.11: «якщо AF немає як створити або імпортувати?»), HTTP + SQL Server: the first node of an empty
/// tree, region roots from the region registry, and the Excel import — preview (new / existing / conflicts, name differences as
/// warnings), all-or-nothing apply, a second import of the same file as a clean no-op, the template, and the access rules.
/// Each test points the NAE tree and the region source at its own registry codes (<see cref="NaeTreeSettings"/>), so the shared
/// test database never sees two tests on one tree.
/// </summary>
/// <remarks>
/// Mutation proof (local, not committed): drop the parent comparison of an existing business key in
/// <c>NaeTreeManualHandler.PlanAsync</c> (<c>match.ParentEntryId != parentId</c>) → the moved NAE is reported as existing,
/// <c>Preview__reports_new_existing_conflicts_and_name_warnings</c> fails on <c>existingElsewhere</c>.
/// </remarks>
[Collection("SqlServer")]
public sealed class NaeTreeManualHttpTests(SqlServerFixture sql)
{
    private const string Password = "Api-Nae-Manual-2026!";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static readonly string[] Full = ["Registry.View", "Registry.EditDefinition", "Registry.EditData", "Registry.Publish"];

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string?[][] Standard =
    [
        ["Atyrau", "Offshore", "Island_A", null, "1085", "Gen 1085", "Ген 1085", null, null],
        ["Atyrau", "Offshore", "Island_A", null, "1086", null, null, null, null],
        ["Mangistau", null, "ZPK 1", null, "1085", "ZPK 1085", null, null, null],
    ];

    private Func<string>? serverErrors;

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("", 403, 403, 403, 403)]
    [InlineData("Registry.View", 200, 403, 403, 403)]
    [InlineData("Registry.EditData", 403, 200, 200, 403)]
    [InlineData("Registry.EditDefinition,Registry.Publish", 403, 403, 403, 403)]
    public async Task Access_matrix__template_needs_view_import_and_roots_need_edit_data_provisioning_needs_definition(
        string permissions, int template, int preview, int rootOnExisting, int rootOnMissing)
    {
        var (provisioned, missing) = (NewCode(), NewCode());
        using (var setup = Factory(provisioned, NewCode()))
        using (var admin = await SignedInAsync(setup, Full))
        {
            await ReadAsync(await RootAsync(admin, "SETUP", null));
        }

        var granted = permissions.Split(',', StringSplitOptions.RemoveEmptyEntries);
        using (var app = Factory(provisioned, NewCode()))
        using (var client = await SignedInAsync(app, granted))
        {
            Assert.Equal((HttpStatusCode)template, (await client.GetAsync(new Uri("/api/v1/nae-tree/import/template", UriKind.Relative))).StatusCode);
            Assert.Equal((HttpStatusCode)preview, (await PostFileAsync(client, "preview", Book(Standard), new { })).StatusCode);
            Assert.Equal((HttpStatusCode)rootOnExisting, (await RootAsync(client, "ATYRAU", null)).StatusCode);
        }

        using (var app = Factory(missing, NewCode()))
        using (var client = await SignedInAsync(app, granted))
        {
            // The registry does not exist yet: the first node also provisions it, which is a definition change.
            Assert.Equal((HttpStatusCode)rootOnMissing, (await RootAsync(client, "ATYRAU", null)).StatusCode);
        }

        Assert.False(await RegistryExistsAsync(missing));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Root__the_first_node_of_an_empty_tree_provisions_the_registry_and_is_idempotent()
    {
        var code = NewCode();
        using var app = Factory(code, NewCode());
        using var client = await SignedInAsync(app, Full);

        var first = await ReadAsync(await RootAsync(client, "Atyrau", "Offshore", "Offshore zone", "Морская зона"));
        Assert.True(first.GetProperty("created").GetBoolean());
        Assert.True(first.GetProperty("registryProvisioned").GetBoolean());

        var tree = await TreeAsync(client, code);
        Assert.Equal(2, tree.Count);
        var region = Assert.Single(tree, n => n.GetProperty("levelCode").GetString() == "REGION");
        Assert.Equal(JsonValueKind.Null, region.GetProperty("parentEntryId").ValueKind);
        Assert.Equal("R_ATYRAU", region.GetProperty("code").GetString());
        Assert.Equal(first.GetProperty("regionEntryId").GetInt64(), region.GetProperty("id").GetInt64());
        var area = Assert.Single(tree, n => n.GetProperty("levelCode").GetString() == "AREA");
        Assert.Equal(region.GetProperty("id").GetInt64(), area.GetProperty("parentEntryId").GetInt64());
        Assert.Contains("Offshore zone", area.ToString(), StringComparison.Ordinal);

        // Again: the same ids, nothing created, nothing renamed.
        var second = await ReadAsync(await RootAsync(client, "ATYRAU", "OFFSHORE", "Other name"));
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.Equal(first.GetProperty("areaEntryId").GetInt64(), second.GetProperty("areaEntryId").GetInt64());
        var after = await TreeAsync(client, code);
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain("Other name", JsonSerializer.Serialize(after), StringComparison.Ordinal);

        // An empty region is refused before anything is written.
        using var empty = await RootAsync(client, "  ", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, empty.StatusCode);
        Assert.Equal("err.ECR-REG-0422.naeRegionRequired", (await ProblemAsync(empty)).GetProperty("messageKey").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Import__apply_builds_the_tree_and_a_second_import_of_the_same_file_is_a_clean_no_op()
    {
        var code = NewCode();
        using var app = Factory(code, NewCode());
        using var client = await SignedInAsync(app, Full);
        var book = Book(Standard);

        var analysis = await ReadAsync(await PostFileAsync(client, "analyse", book, null));
        Assert.Equal(3, analysis.GetProperty("rowsRead").GetInt32());
        Assert.True(analysis.GetProperty("canApply").GetBoolean());

        var preview = await ReadAsync(await PostFileAsync(client, "preview", book, new { }));
        Assert.Equal((2, 0), Count(preview, "REGION"));
        Assert.Equal((1, 0), Count(preview, "AREA"));
        Assert.Equal((2, 0), Count(preview, "SITE"));
        Assert.Equal((3, 0), Count(preview, "SRC"));
        Assert.Empty(preview.GetProperty("issues").EnumerateArray());
        Assert.False(await RegistryExistsAsync(code));

        var applied = await ReadAsync(await PostFileAsync(client, "apply", book, new { expectedFingerprint = preview.GetProperty("fingerprint").GetString() }));
        Assert.True(applied.GetProperty("applied").GetBoolean());
        Assert.True(applied.GetProperty("registryProvisioned").GetBoolean());
        var tree = await TreeAsync(client, code);
        Assert.Equal(8, tree.Count);
        var src = tree.Where(n => n.GetProperty("levelCode").GetString() == "SRC").ToList();
        Assert.Equal(3, src.Count);
        Assert.All(src, n => Assert.Equal("SITE", tree.Single(p => p.GetProperty("id").GetInt64() == n.GetProperty("parentEntryId").GetInt64()).GetProperty("levelCode").GetString()));

        // The same file again: everything exists, no finding at all, nothing written.
        var again = await ReadAsync(await PostFileAsync(client, "preview", book, new { }));
        Assert.Equal((0, 3), Count(again, "SRC"));
        Assert.Equal((0, 2), Count(again, "SITE"));
        Assert.Empty(again.GetProperty("issues").EnumerateArray());
        Assert.True(again.GetProperty("canApply").GetBoolean());
        var noOp = await ReadAsync(await PostFileAsync(client, "apply", book, new { expectedFingerprint = again.GetProperty("fingerprint").GetString() }));
        Assert.False(noOp.GetProperty("applied").GetBoolean());
        Assert.Equal(8, (await TreeAsync(client, code)).Count);
        Assert.Equal(1, await AuditCountAsync(code, "NaeExcelImport"));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Preview__reports_new_existing_conflicts_and_name_warnings_and_apply_with_a_conflict_writes_nothing()
    {
        var code = NewCode();
        using var app = Factory(code, NewCode());
        using var client = await SignedInAsync(app, Full);
        await ReadAsync(await PostFileAsync(client, "apply", Book(Standard), new { }));
        var before = await TreeAsync(client, code);

        var changed = Book(
        [
            ["Atyrau", "Offshore", "Island_A", null, "1085", "Generator 1085", "Ген 1085", null, null],   // existing, other English name
            ["Atyrau", "Offshore", "Island_B", null, "1086", null, null, null, null],                     // moved: conflict
            ["Atyrau", "Onshore", "GTP", null, "2001", null, null, null, null],                           // new
        ]);

        var preview = await ReadAsync(await PostFileAsync(client, "preview", changed, new { }));
        Assert.False(preview.GetProperty("canApply").GetBoolean());
        Assert.Equal((1, 1), Count(preview, "SRC"));
        Assert.Equal((2, 1), Count(preview, "SITE"));
        var issues = preview.GetProperty("issues").EnumerateArray().ToList();
        var warning = Assert.Single(issues, i => i.GetProperty("severity").GetString() == "warning");
        Assert.Equal(("nameDiffers", 2, "Name_En"), (warning.GetProperty("code").GetString(), warning.GetProperty("row").GetInt32(), warning.GetProperty("column").GetString()));
        var conflict = Assert.Single(issues, i => i.GetProperty("severity").GetString() == "error");
        Assert.Equal(("existingElsewhere", 3, "1086"), (conflict.GetProperty("code").GetString(), conflict.GetProperty("row").GetInt32(), conflict.GetProperty("naeCode").GetString()));

        // All or nothing: not even the new site and NAE of the clean rows are written.
        using var refused = await PostFileAsync(client, "apply", changed, new { expectedFingerprint = preview.GetProperty("fingerprint").GetString() });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("err.ECR-IMP-0422.naeImportHasErrors", (await ProblemAsync(refused)).GetProperty("messageKey").GetString());
        Assert.Equal(before.Count, (await TreeAsync(client, code)).Count);

        // A name difference alone is only a warning: the apply goes through and keeps the stored name.
        var renamed = Book([["Atyrau", "Offshore", "Island_A", null, "1085", "Generator 1085", null, null, null], ["Atyrau", "Onshore", "GTP", null, "2001", null, null, null, null]]);
        var ok = await ReadAsync(await PostFileAsync(client, "preview", renamed, new { }));
        Assert.True(ok.GetProperty("canApply").GetBoolean());
        await ReadAsync(await PostFileAsync(client, "apply", renamed, new { expectedFingerprint = ok.GetProperty("fingerprint").GetString() }));
        var after = await TreeAsync(client, code);
        Assert.Equal(before.Count + 3, after.Count);
        Assert.DoesNotContain("Generator 1085", JsonSerializer.Serialize(after), StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Apply__a_changed_plan_or_a_failure_inside_the_transaction_writes_nothing()
    {
        var code = NewCode();
        using var app = new EcrApiFactory(sql)
        {
            ExtraServices = s =>
            {
                s.AddSingleton(new NaeTreeSettings(code, NewCode()));
                s.AddScoped<IAuditWriter>(sp => new FailingAudit(ActivatorUtilities.CreateInstance<AuditWriter>(sp)));
            },
        };
        serverErrors = () => app.ErrorsText;
        using var client = await SignedInAsync(app, Full);
        await ReadAsync(await RootAsync(client, "ATYRAU", null));

        using (var stale = await PostFileAsync(client, "apply", Book(Standard), new { expectedFingerprint = "00" }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, stale.StatusCode);
            Assert.Equal("err.ECR-IMP-0422.regPlanChanged", (await ProblemAsync(stale)).GetProperty("messageKey").GetString());
        }

        FailingAudit.Armed = true;
        try
        {
            using var failed = await PostFileAsync(client, "apply", Book(Standard), new { });
            Assert.False(failed.IsSuccessStatusCode);
        }
        finally
        {
            FailingAudit.Armed = false;
        }

        // Only the root from before: the levels written before the failing audit record are rolled back.
        Assert.Single(await TreeAsync(client, code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Template__is_the_header_row_and_reads_back_as_an_empty_clean_file()
    {
        using var app = Factory(NewCode(), NewCode());
        using var client = await SignedInAsync(app, Full);

        using var response = await client.GetAsync(new Uri("/api/v1/nae-tree/import/template", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Xlsx, response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        using (var book = new XLWorkbook(new MemoryStream(bytes)))
        {
            var sheet = book.Worksheets.First();
            Assert.Equal(
                ["Region", "Area", "Site", "Equipment", "NAE code", "Name_En", "Name_Ru", "Name_Kz", "On_Offshore"],
                Enumerable.Range(1, 9).Select(c => sheet.Cell(1, c).GetString()).ToArray());
        }

        var analysis = await ReadAsync(await PostFileAsync(client, "analyse", bytes, null));
        Assert.Equal(0, analysis.GetProperty("rowsRead").GetInt32());
        Assert.True(analysis.GetProperty("canApply").GetBoolean());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task SeedRoots__creates_region_roots_from_the_region_registry_only_in_an_empty_tree()
    {
        var (code, regions) = (NewCode(), NewCode());
        await SeedRegionsAsync(regions, ("REG_001", "Atyrau", "Атырау"), ("REG_002", "Mangistau", "Мангистау"));
        using var app = Factory(code, regions);
        using var client = await SignedInAsync(app, Full);

        var result = await ReadAsync(await client.PostAsync(new Uri("/api/v1/nae-tree/seed-roots", UriKind.Relative), null));
        Assert.Equal(["ATYRAU", "MANGISTAU"], result.GetProperty("created").EnumerateArray().Select(c => c.GetString()!).ToArray());
        Assert.True(result.GetProperty("registryProvisioned").GetBoolean());
        var tree = await TreeAsync(client, code);
        Assert.Equal(2, tree.Count);
        Assert.All(tree, n => Assert.Equal(("REGION", JsonValueKind.Null), (n.GetProperty("levelCode").GetString(), n.GetProperty("parentEntryId").ValueKind)));
        await using (var db = new EcrDbContext(Options()))
        {
            var id = await db.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
            var names = (await db.RegistryEntries.Where(e => e.RegistryDefId == id).ToListAsync()).Select(e => e.DisplayL10n.Get("ru")).ToList();
            Assert.Contains("Мангистау", names);
        }

        // Never into a tree that has nodes: the second call is refused and changes nothing.
        using var again = await client.PostAsync(new Uri("/api/v1/nae-tree/seed-roots", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
        Assert.Equal("err.ECR-REG-0422.naeTreeNotEmpty", (await ProblemAsync(again)).GetProperty("messageKey").GetString());
        Assert.Equal(2, (await TreeAsync(client, code)).Count);

        // Seeded roots and imported rows are the same nodes (same technical codes).
        var preview = await ReadAsync(await PostFileAsync(client, "preview", Book(Standard), new { }));
        Assert.Equal((0, 2), Count(preview, "REGION"));
    }

    // ---- fixtures ------------------------------------------------------------------------------------------------------

    private sealed class FailingAudit(IAuditWriter inner) : IAuditWriter
    {
        public static volatile bool Armed;

        public Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct) => inner.WriteCellChangesAsync(changes, ct);

        public Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct)
            => Armed && change.Operation == "NaeExcelImport"
                ? throw new InvalidOperationException("Injected failure after the node writes.")
                : inner.WriteStructureChangeAsync(change, ct);

        public Task WriteStructureChangesAsync(IReadOnlyList<StructureChangeRecord> changes, CancellationToken ct) => inner.WriteStructureChangesAsync(changes, ct);

        public Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct) => inner.WriteSecurityEventAsync(evt, ct);

        public Task WriteSecurityEventsAsync(IReadOnlyList<SecurityEventRecord> events, CancellationToken ct) => inner.WriteSecurityEventsAsync(events, ct);

        public Task WriteIndependentSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct) => inner.WriteIndependentSecurityEventAsync(evt, ct);

        public Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct) => inner.WritePublicationEventAsync(evt, ct);
    }

    /// <summary>The Area list is chosen from (human 04.11); these tests type areas freely, so they point at a list that does not exist.</summary>
    private static Ecr.Application.Permits.AirLimits.AirLimitsSettings NoAreaList()
        => Ecr.Application.Permits.AirLimits.AirLimitsSettings.Default with { AreaRegistryCode = "NOAREAS_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant() };

    private static string NewCode() => $"NAEM{Guid.NewGuid():N}"[..14].ToUpperInvariant();

    private EcrApiFactory Factory(string code, string regions)
    {
        var app = new EcrApiFactory(sql) { ExtraServices = s => { s.AddSingleton(new NaeTreeSettings(code, regions)); s.AddSingleton(NoAreaList()); } };
        serverErrors = () => app.ErrorsText;
        return app;
    }

    private static byte[] Book(string?[][] rows)
    {
        using var book = new XLWorkbook();
        var sheet = book.Worksheets.Add("NAE");
        string[] header = ["Region", "Area", "Site", "Equipment", "NAE code", "Name_En", "Name_Ru", "Name_Kz", "On_Offshore"];
        for (var c = 0; c < header.Length; c++)
        {
            sheet.Cell(1, c + 1).Value = header[c];
        }

        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c] is { } text)
                {
                    sheet.Cell(r + 2, c + 1).Value = text;
                }
            }
        }

        using var output = new MemoryStream();
        book.SaveAs(output);
        return output.ToArray();
    }

    private static Task<HttpResponseMessage> RootAsync(HttpClient client, string region, string? area, string? en = null, string? ru = null)
        => client.PostAsJsonAsync(
            new Uri("/api/v1/nae-tree/roots", UriKind.Relative),
            new { regionCode = region, areaCode = area, nameEn = en, nameRu = ru, nameKz = (string?)null });

    private static async Task<HttpResponseMessage> PostFileAsync(HttpClient client, string step, byte[] bytes, object? request)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(Xlsx);
        content.Add(file, "file", "nae-tree.xlsx");
        if (request is not null)
        {
            content.Add(new StringContent(JsonSerializer.Serialize(request, Web)), "request");
        }

        return await client.PostAsync(new Uri($"/api/v1/nae-tree/import/{step}", UriKind.Relative), content);
    }

    private async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text} | {serverErrors?.Invoke()}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }
    }

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task<List<JsonElement>> TreeAsync(HttpClient client, string code)
        => [.. (await ReadAsync(await client.GetAsync(new Uri($"/api/v1/registries/{code}/tree", UriKind.Relative)))).GetProperty("nodes").EnumerateArray()];

    private static (int Create, int Existing) Count(JsonElement report, string level)
    {
        var row = report.GetProperty("levels").EnumerateArray().Single(l => l.GetProperty("levelCode").GetString() == level);
        return (row.GetProperty("create").GetInt32(), row.GetProperty("existing").GetInt32());
    }

    private async Task<bool> RegistryExistsAsync(string code)
    {
        await using var db = new EcrDbContext(Options());
        return await db.RegistryDefs.AnyAsync(d => d.Code == code);
    }

    private async Task<int> AuditCountAsync(string code, string operation)
    {
        await using var db = new EcrDbContext(Options());
        var id = await db.RegistryDefs.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
        return await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM aud.StructureChange WHERE Operation = {operation} AND EntityId = {id}").SingleAsync();
    }

    private async Task SeedRegionsAsync(string code, params (string Code, string En, string Ru)[] regions)
    {
        await using var db = new EcrDbContext(Options());
        var registry = new RegistryDef(EcrCode.Create(code), Name("Regions"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        var ordinal = 0;
        foreach (var (entryCode, en, ru) in regions)
        {
            db.RegistryEntries.Add(new RegistryEntry(
                registry.Id, EcrCode.Create(entryCode), new LocalizedText(new Dictionary<string, string> { ["en"] = en, ["ru"] = ru }), ++ordinal, DateTime.UtcNow));
        }

        await db.SaveChangesAsync();
    }

    private DbContextOptions<EcrDbContext> Options()
        => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"naem_{Guid.NewGuid():N}"[..20];

        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();

            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), Name("NAE manual test"));
            db.Roles.Add(role);
            await db.SaveChangesAsync();

            foreach (var permission in permissions)
            {
                db.RolePermissions.Add(new RolePermission(role.Id, permission));
            }

            db.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, principalSid: null));
            await db.SaveChangesAsync();
        }

        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative),
            new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }
}
