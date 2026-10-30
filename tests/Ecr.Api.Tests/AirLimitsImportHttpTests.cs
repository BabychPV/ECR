// tests/Ecr.Api.Tests/AirLimitsImportHttpTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Adapters.Tests.Excel.RegistryImport;
using Ecr.Application.Ports;
using Ecr.Application.Permits.AirLimits;
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
using Xunit.Abstractions;

namespace Ecr.Api.Tests;

/// <summary>
/// NAE plan N3 end to end (HTTP + SQL Server): provisioning of the "Air Limits Import" permit type, the wizard backend (analyse,
/// preview, apply) with the SYNTHETIC ERA table 3.3 workbook (373 sources / 2,624 substance rows / 55 substances / 27 groups),
/// preview == commit, all or nothing, the replace scope with confirmation of other permits, the access matrix and "where used".
/// </summary>
/// <remarks>
/// Mutation proofs (local, not committed): drop the <c>ConfirmedOtherOwners</c> check in <c>RegistryImportService.CommitAsync</c> →
/// the replace-scope test applies without confirmation and fails; set <c>AirLimitsImportHandler.Permission</c> to
/// <c>Registry.View</c> → the access matrix returns 200 for a reader; drop the delete loop in <c>AirLimitsImportTarget</c> →
/// the re-import test doubles the rows.
/// </remarks>
[Collection("SqlServer")]
public sealed class AirLimitsImportHttpTests(SqlServerFixture sql, ITestOutputHelper output)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool prepared;
    private static long siteNodeId;

    private static readonly string[] Admin =
    [
        "Registry.View", "Registry.EditDefinition", "Registry.EditData", "Registry.Publish", "Permit.EditType", "Permit.PublishType",
    ];

    private static readonly string[] Importer = ["Registry.View", "Registry.EditData"];

    private static readonly SyntheticEraTable33.Workbook Synthetic = SyntheticEraTable33.Build();

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [InlineData("", 403, 403, 403)]
    [InlineData("Registry.View", 403, 403, 200)]
    [InlineData("Registry.EditData", 200, 200, 403)]
    public async Task Access_matrix__import_needs_edit_data_and_the_card_needs_view(string permissions, int analyse, int preview, int card)
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var admin = await SignedInAsync(app, Admin);
        var permitId = await NewPermitAsync(admin, app);
        using var client = await SignedInAsync(app, permissions.Split(',', StringSplitOptions.RemoveEmptyEntries));

        var a = await PostFileAsync(client, "/api/v1/air-limits/import/analyse", Synthetic.Bytes, new { });
        Assert.Equal((HttpStatusCode)analyse, a.StatusCode);
        var p = await PostFileAsync(client, $"/api/v1/permits/{permitId}/air-limits/import/preview", Synthetic.Bytes, new { region = "ACCESS" });
        Assert.Equal((HttpStatusCode)preview, p.StatusCode);
        var c = await client.GetAsync(new Uri($"/api/v1/permits/{permitId}/air-limits", UriKind.Relative));
        Assert.Equal((HttpStatusCode)card, c.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Provision__is_idempotent_and_ships_the_built_in_profile()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);

        var again = await ReadAsync(app, await client.PostAsync(new Uri("/api/v1/air-limits/provision", UriKind.Relative), null));
        Assert.Equal("AIR_LIMITS", again.GetProperty("typeCode").GetString());
        Assert.False(again.GetProperty("typeCreated").GetBoolean());
        Assert.True(again.GetProperty("published").GetBoolean());
        Assert.False(again.GetProperty("profileCreated").GetBoolean());

        var profiles = await ReadAsync(app, await client.GetAsync(new Uri("/api/v1/air-limits/import-profiles", UriKind.Relative)));
        Assert.Contains(profiles.EnumerateArray(), p => p.GetProperty("code").GetString() == "ERA_TABLE_33" && p.GetProperty("builtIn").GetBoolean());

        var profile = await ReadAsync(app, await client.GetAsync(new Uri("/api/v1/air-limits/import-profiles/ERA_TABLE_33", UriKind.Relative)));
        Assert.Equal("naeCode", profile.GetProperty("spec").GetProperty("parentKeyField").GetString());

        using var reader = await SignedInAsync(app, "Registry.View");
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync(new Uri("/api/v1/air-limits/provision", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(
            new Uri("/api/v1/air-limits/import-profiles/X1", UriKind.Relative),
            new { nameL10n = new Dictionary<string, string> { ["en"] = "x" }, spec = profile.GetProperty("spec"), rowVersion = (string?)null })).StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Golden__synthetic_workbook_through_the_endpoints_gives_373_2624_55_27()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);

        // Step 1-3: layout, header block, mapped columns.
        var analysis = (await ReadAsync(app, await PostFileAsync(client, "/api/v1/air-limits/import/analyse", Synthetic.Bytes, new { })))
            .GetProperty("analysis");
        Assert.True(analysis.GetProperty("canContinue").GetBoolean());
        Assert.Equal(373, analysis.GetProperty("stats").GetProperty("parents").GetInt32());
        Assert.Equal(2624, analysis.GetProperty("stats").GetProperty("children").GetInt32());
        Assert.Equal(55, analysis.GetProperty("stats").GetProperty("distinctChildKeys").GetInt32());
        Assert.Equal(27, analysis.GetProperty("stats").GetProperty("groupRows").GetInt32());
        Assert.Contains(analysis.GetProperty("header").GetProperty("mapped").EnumerateArray(), m => m.GetProperty("field").GetString() == "tonsPerYear");

        // Step 4, first look: no NAE node exists in the new region, so every source is an error with its row and column.
        var first = (await ReadAsync(app, await PostFileAsync(client, Preview(permitId), Synthetic.Bytes, new { region, area = "OFFSHORE" }))).GetProperty("preview");
        Assert.False(first.GetProperty("canApply").GetBoolean());
        Assert.Equal(373, first.GetProperty("missing").GetArrayLength());
        var missing = first.GetProperty("issues").EnumerateArray().First(i => i.GetProperty("messageKey").GetString() == "err.ECR-IMP-0422.regReferenceMissing");
        Assert.Equal("naeCode", missing.GetProperty("field").GetString());
        Assert.True(missing.GetProperty("row").GetInt32() > 0);
        Assert.False(string.IsNullOrEmpty(missing.GetProperty("column").GetString()));

        // Resolve: create every missing NAE under the site node. Substances all exist (the registry holds the 55 codes).
        var resolutions = first.GetProperty("missing").EnumerateArray()
            .Select(m => new { kind = "nae", code = m.GetProperty("code").GetString(), action = "create", createUnder = siteNodeId.ToString(CultureInfo.InvariantCulture) })
            .ToArray();
        var options = new { region, area = "OFFSHORE", resolutions };

        var preview = (await ReadAsync(app, await PostFileAsync(client, Preview(permitId), Synthetic.Bytes, options))).GetProperty("preview");
        Assert.True(preview.GetProperty("canApply").GetBoolean(), preview.GetProperty("issues").ToString());
        Assert.Equal(373, preview.GetProperty("diff").GetProperty("newParents").GetInt32());
        Assert.False(preview.GetProperty("replace").GetProperty("confirmationRequired").GetBoolean());

        // The one deliberately empty t/year cell: a per-row WARNING with row and column, not an error, not silent.
        var warnings = preview.GetProperty("issues").EnumerateArray().Where(i => i.GetProperty("severity").GetString()!.Equals("warning", StringComparison.OrdinalIgnoreCase)).ToList();
        var empty = Assert.Single(warnings, w => w.GetProperty("messageKey").GetString() == "err.ECR-IMP-0422.regValueEmpty");
        Assert.Equal("tonsPerYear", empty.GetProperty("field").GetString());
        Assert.True(empty.GetProperty("row").GetInt32() > 0);

        // Preview == commit: the apply needs the previewed fingerprint and applies exactly that plan.
        var fingerprint = preview.GetProperty("fingerprint").GetString();
        var applied = await ReadAsync(app, await PostFileAsync(client, Apply(permitId), Synthetic.Bytes, new { region, area = "OFFSHORE", resolutions, expectedFingerprint = fingerprint }));
        Assert.Equal(fingerprint, applied.GetProperty("fingerprint").GetString());
        Assert.Equal(373, applied.GetProperty("sources").GetInt32());
        Assert.Equal(2624, applied.GetProperty("substances").GetInt32());
        Assert.Equal(373, applied.GetProperty("createdNae").GetInt32());

        // The permit card: every source with its substances, totals computed from the rows.
        var card = await ReadAsync(app, await client.GetAsync(new Uri($"/api/v1/permits/{permitId}/air-limits", UriKind.Relative)));
        var sources = card.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(373, sources.Count);
        Assert.Equal(2624, card.GetProperty("substanceRows").GetInt32());
        Assert.Equal(55, card.GetProperty("totals").GetArrayLength());
        Assert.Equal(2624, sources.Sum(s => s.GetProperty("substances").GetArrayLength()));

        var modelTons = Synthetic.Model.SelectMany(m => m.Substances).Sum(s => s.TonsPerYear ?? 0m);
        var cardTons = card.GetProperty("totals").EnumerateArray().Sum(t => Dec(t.GetProperty("tonsPerYear")));
        Assert.Equal(modelTons, cardTons);

        // Values survive: canonical Latin source type (T / P1), a value of 3.5e-12 is not rounded, the diffuse source has no diameter.
        var byNae = sources.ToDictionary(s => s.GetProperty("naeCode").GetString()!);
        Assert.All(Synthetic.Model, m =>
        {
            var s = byNae[m.Nae];
            Assert.Equal(m.Type, s.GetProperty("sourceType").GetString());
            Assert.Equal(m.Name, s.GetProperty("sourceName").GetString());
            Assert.Equal(m.Height, Dec(s.GetProperty("height")));
            Assert.Equal(m.Diameter is null, s.GetProperty("diameter").ValueKind == JsonValueKind.Null);
            Assert.Equal(m.Substances.Count, s.GetProperty("substances").GetArrayLength());
        });
        var tiny = Synthetic.Model.SelectMany(m => m.Substances.Select(s => (m.Nae, s))).First(x => x.s.GramsPerSecond == 3.5e-12m);
        Assert.Contains(
            byNae[tiny.Nae].GetProperty("substances").EnumerateArray(),
            s => s.GetProperty("substanceCode").GetString() == tiny.s.Code && Dec(s.GetProperty("gramsPerSecond")) == 3.5e-12m);

        // "Where used" now answers: the NAE node is used by this permit, with the number of its substance rows.
        var sample = sources[0];
        var refs = await ReadAsync(app, await client.GetAsync(new Uri(
            $"/api/v1/registries/{NaeTreeSchema.RegistryCode}/entries/{sample.GetProperty("naeEntryId").GetInt64()}/references", UriKind.Relative)));
        var use = Assert.Single(refs.GetProperty("items").EnumerateArray());
        Assert.Equal(permitId, use.GetProperty("permitId").GetInt64());
        Assert.Equal("AIR_LIMITS", use.GetProperty("permitTypeCode").GetString());
        Assert.Equal(sample.GetProperty("substances").GetArrayLength(), use.GetProperty("substanceCount").GetInt32());

        var substanceEntryId = sample.GetProperty("substances")[0].GetProperty("substanceEntryId").GetInt64();
        var substanceRefs = await ReadAsync(app, await client.GetAsync(new Uri(
            $"/api/v1/registries/{Substances}/entries/{substanceEntryId}/references", UriKind.Relative)));
        Assert.Contains(substanceRefs.GetProperty("items").EnumerateArray(), i => i.GetProperty("permitId").GetInt64() == permitId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Reimport__replaces_the_permits_own_rows_without_duplicates()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        var resolutions = await ResolveAllAsync(app, client, permitId, region);

        await ApplyAsync(app, client, permitId, new { region, area = "OFFSHORE", resolutions });

        var preview = (await ReadAsync(app, await PostFileAsync(client, Preview(permitId), Synthetic.Bytes, new { region, area = "OFFSHORE" }))).GetProperty("preview");
        Assert.True(preview.GetProperty("canApply").GetBoolean());
        Assert.Equal(373, preview.GetProperty("replace").GetProperty("thisOwner").GetProperty("parents").GetInt32());
        Assert.Equal(373, preview.GetProperty("diff").GetProperty("unchangedParents").GetInt32());
        Assert.Equal(0, preview.GetProperty("diff").GetProperty("newParents").GetInt32());

        var again = await ApplyAsync(app, client, permitId, new { region, area = "OFFSHORE" });
        Assert.Equal(373, again.GetProperty("replacedOwn").GetInt32());

        var card = await ReadAsync(app, await client.GetAsync(new Uri($"/api/v1/permits/{permitId}/air-limits", UriKind.Relative)));
        Assert.Equal(373, card.GetProperty("sources").GetArrayLength());
        Assert.Equal(2624, card.GetProperty("substanceRows").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task ReplaceScope__other_permits_rows_for_the_same_nae_need_an_explicit_confirmation()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var region = NewRegion();
        var first = await NewPermitAsync(client, app);
        var second = await NewPermitAsync(client, app);
        var resolutions = await ResolveAllAsync(app, client, first, region);
        await ApplyAsync(app, client, first, new { region, area = "OFFSHORE", resolutions });

        // The second permit imports the same NAE: the preview lists the first permit with its counts.
        var preview = (await ReadAsync(app, await PostFileAsync(client, Preview(second), Synthetic.Bytes, new { region, area = "OFFSHORE" }))).GetProperty("preview");
        Assert.True(preview.GetProperty("canApply").GetBoolean());
        var replace = preview.GetProperty("replace");
        Assert.True(replace.GetProperty("confirmationRequired").GetBoolean());
        var other = Assert.Single(replace.GetProperty("otherOwners").EnumerateArray());
        Assert.Equal(first.ToString(CultureInfo.InvariantCulture), other.GetProperty("ownerKey").GetString());
        Assert.Equal(373, other.GetProperty("parents").GetInt32());
        Assert.Equal(2624, other.GetProperty("children").GetInt32());
        Assert.False(string.IsNullOrEmpty(other.GetProperty("label").GetString()));

        // Without the confirmation nothing is written.
        var refused = await PostFileAsync(client, Apply(second), Synthetic.Bytes, new { region, area = "OFFSHORE", expectedFingerprint = preview.GetProperty("fingerprint").GetString() });
        await AssertErrorAsync(app, refused, HttpStatusCode.UnprocessableEntity, "err.ECR-IMP-0422.regReplaceNotConfirmed");
        Assert.Equal(373, (await CardAsync(app, client, first)).GetProperty("sources").GetArrayLength());
        Assert.Equal(0, (await CardAsync(app, client, second)).GetProperty("sources").GetArrayLength());

        // With it the first permit loses the rows and the second gets them.
        var done = await ApplyAsync(app, client, second, new
        {
            region,
            area = "OFFSHORE",
            confirmedOtherOwners = new[] { first.ToString(CultureInfo.InvariantCulture) },
            expectedFingerprint = preview.GetProperty("fingerprint").GetString(),
        });
        Assert.Equal(373, Assert.Single(done.GetProperty("replacedOthers").EnumerateArray()).GetProperty("parents").GetInt32());
        Assert.Equal(0, (await CardAsync(app, client, first)).GetProperty("sources").GetArrayLength());
        Assert.Equal(373, (await CardAsync(app, client, second)).GetProperty("sources").GetArrayLength());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task AllOrNothing__an_unknown_or_empty_substance_blocks_the_whole_import_with_row_and_column()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        var resolutions = await ResolveAllAsync(app, client, permitId, region);

        var analysis = (await ReadAsync(app, await PostFileAsync(client, "/api/v1/air-limits/import/analyse", Synthetic.Bytes, new { }))).GetProperty("analysis");
        var column = analysis.GetProperty("header").GetProperty("mapped").EnumerateArray()
            .Single(m => m.GetProperty("field").GetString() == "substanceCode").GetProperty("columnLetter").GetString()!;
        var row = Synthetic.FirstSourceRow;

        var unknown = Edit(Synthetic.Bytes, column, row, "9999");
        var p1 = (await ReadAsync(app, await PostFileAsync(client, Preview(permitId), unknown, new { region, area = "OFFSHORE", resolutions }))).GetProperty("preview");
        Assert.False(p1.GetProperty("canApply").GetBoolean());
        var e1 = p1.GetProperty("issues").EnumerateArray().Single(i => i.GetProperty("severity").GetString()!.Equals("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("err.ECR-IMP-0422.regReferenceMissing", e1.GetProperty("messageKey").GetString());
        Assert.Equal((row, column, "substanceCode"), (e1.GetProperty("row").GetInt32(), e1.GetProperty("column").GetString()!, e1.GetProperty("field").GetString()!));

        var blank = Edit(Synthetic.Bytes, column, row, null);
        var p2 = (await ReadAsync(app, await PostFileAsync(client, Preview(permitId), blank, new { region, area = "OFFSHORE", resolutions }))).GetProperty("preview");
        var e2 = p2.GetProperty("issues").EnumerateArray().First(i => i.GetProperty("severity").GetString()!.Equals("error", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((row, column), (e2.GetProperty("row").GetInt32(), e2.GetProperty("column").GetString()!));

        // Apply refuses and writes nothing: no source, no substance, no NAE node of this region.
        var refused = await PostFileAsync(client, Apply(permitId), unknown, new { region, area = "OFFSHORE", resolutions });
        await AssertErrorAsync(app, refused, HttpStatusCode.UnprocessableEntity, "err.ECR-IMP-0422.regRejected");
        Assert.Equal(0, (await CardAsync(app, client, permitId)).GetProperty("sources").GetArrayLength());
        var tree = await ReadAsync(app, await client.GetAsync(new Uri($"/api/v1/registries/{NaeTreeSchema.RegistryCode}/tree", UriKind.Relative)));
        Assert.DoesNotContain(tree.GetProperty("nodes").EnumerateArray(), n => (n.GetProperty("code").GetString() ?? "").Contains($"_{region}_", StringComparison.Ordinal));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Request_errors__region_is_required_and_a_foreign_permit_is_refused()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var permitId = await NewPermitAsync(client, app);

        await AssertErrorAsync(app, await PostFileAsync(client, Preview(permitId), Synthetic.Bytes, new { }), HttpStatusCode.UnprocessableEntity, "err.ECR-IMP-0422.airScopeInvalid");
        await AssertErrorAsync(
            app, await PostFileAsync(client, Preview(permitId), Synthetic.Bytes, new { region = "X1", profileCode = "NOPE" }),
            HttpStatusCode.UnprocessableEntity, "err.ECR-IMP-0422.airProfileUnknown");
        Assert.Equal(HttpStatusCode.NotFound, (await PostFileAsync(client, Preview(999_999_999), Synthetic.Bytes, new { region = "X1" })).StatusCode);
    }

    // ---------- N3b: the apply is set-based ----------

    /// <summary>
    /// Ceiling for the wall time of ONE apply of the full golden file (first import and replace). The real numbers are an order of
    /// magnitude lower (see the commit); the ceiling is CI-safe and still far below the per-entry deletes it replaced (3+ minutes).
    /// </summary>
    private static readonly TimeSpan ApplyCeiling = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ceiling for SQL commands of ONE apply of the full file: the work is chunked (EF insert batches of ~42 rows for ~30k rows) and
    /// does not grow per deleted entry. The per-entry delete it replaced issued ~15 commands for each of the 2,997 rows.
    /// </summary>
    private const int ApplyCommandCeiling = 3000;

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Apply__full_file_first_import_and_replace_stay_inside_the_time_and_query_budget()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        var bystander = await NewPermitAsync(client, app);
        var resolutions = await ResolveAsync(app, client, permitId, region, "OFFSHORE", Synthetic.Bytes);

        // Another permit in ANOTHER region: its rows must come through both applies untouched.
        var otherRegion = NewRegion();
        var otherResolutions = await ResolveAsync(app, client, bystander, otherRegion, "OFFSHORE", Synthetic.Bytes);
        await ApplyBytesAsync(app, client, bystander, Synthetic.Bytes, new { region = otherRegion, area = "OFFSHORE", resolutions = otherResolutions });

        using var counter = new SqlClientCommandCounter();

        counter.Tally.Reset();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = await ApplyBytesAsync(app, client, permitId, Synthetic.Bytes, new { region, area = "OFFSHORE", resolutions });
        var firstTime = clock.Elapsed;
        var firstCommands = counter.Tally.Snapshot();
        Assert.Equal(373, first.GetProperty("sources").GetInt32());
        Assert.Equal(2624, first.GetProperty("substances").GetInt32());

        counter.Tally.Reset();
        clock.Restart();
        var again = await ApplyBytesAsync(app, client, permitId, Synthetic.Bytes, new { region, area = "OFFSHORE" });
        var replaceTime = clock.Elapsed;
        var replaceCommands = counter.Tally.Snapshot();
        Assert.Equal(373, again.GetProperty("replacedOwn").GetInt32());

        output.WriteLine($"first import: {firstTime.TotalSeconds:F1} s, {firstCommands.Total} commands");
        output.WriteLine($"replace:      {replaceTime.TotalSeconds:F1} s, {replaceCommands.Total} commands");
        output.WriteLine(replaceCommands.Format());

        Assert.True(firstTime <= ApplyCeiling, $"first import took {firstTime.TotalSeconds:F1} s (ceiling {ApplyCeiling.TotalSeconds} s)");
        Assert.True(replaceTime <= ApplyCeiling, $"replace took {replaceTime.TotalSeconds:F1} s (ceiling {ApplyCeiling.TotalSeconds} s)");
        Assert.True(firstCommands.Total <= ApplyCommandCeiling, $"first import issued {firstCommands.Total} commands\n{firstCommands.Format()}");
        Assert.True(replaceCommands.Total <= ApplyCommandCeiling, $"replace issued {replaceCommands.Total} commands\n{replaceCommands.Format()}");

        // Correctness after the replace: exactly one copy of everything, the values of the model, nothing of the bystander touched.
        var card = await CardAsync(app, client, permitId);
        var sources = card.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(373, sources.Count);
        Assert.Equal(373, sources.Select(s => s.GetProperty("naeCode").GetString()).Distinct().Count());
        Assert.Equal(2624, card.GetProperty("substanceRows").GetInt32());
        Assert.Equal(2624, sources.Sum(s => s.GetProperty("substances").GetArrayLength()));
        Assert.Equal(
            Synthetic.Model.SelectMany(m => m.Substances).Sum(s => s.TonsPerYear ?? 0m),
            card.GetProperty("totals").EnumerateArray().Sum(t => Dec(t.GetProperty("tonsPerYear"))));

        var other = await CardAsync(app, client, bystander);
        Assert.Equal(373, other.GetProperty("sources").GetArrayLength());
        Assert.Equal(2624, other.GetProperty("substanceRows").GetInt32());

        // The replaced rows are soft-deleted (history kept), not physically gone: 373 + 2,624 per generation.
        await using var db = new EcrDbContext(Options());
        var deleted = await db.RegistryEntries.CountAsync(e => e.IsDeleted && e.DeletedByUserId != null && e.DeletedAt != null
            && (e.Code != null) && db.RegistryDefs.Any(d => d.Id == e.RegistryDefId && (d.Code == AirLimitsSchema.SourcesRegistry || d.Code == AirLimitsSchema.SubstancesRegistry)));
        Assert.True(deleted >= 373 + 2624, $"soft-deleted air rows: {deleted}");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Replace__scope_leaves_other_areas_and_other_permits_alone()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        var neighbour = await NewPermitAsync(client, app);
        var small = Small(Synthetic.Bytes, 40);

        var offshore = await ResolveAsync(app, client, permitId, region, "OFFSHORE", small);
        var inOffshore = await ApplyBytesAsync(app, client, permitId, small, new { region, area = "OFFSHORE", resolutions = offshore });
        var onshore = await ResolveAsync(app, client, permitId, region, "ONSHORE", small);
        await ApplyBytesAsync(app, client, permitId, small, new { region, area = "ONSHORE", resolutions = onshore });
        var otherRegion = NewRegion();
        var theirs = await ResolveAsync(app, client, neighbour, otherRegion, "OFFSHORE", small);
        await ApplyBytesAsync(app, client, neighbour, small, new { region = otherRegion, area = "OFFSHORE", resolutions = theirs });

        var sources = inOffshore.GetProperty("sources").GetInt32();
        var substances = inOffshore.GetProperty("substances").GetInt32();
        Assert.True(sources > 3 && substances > sources);
        var before = await IdsAsync(app, client, permitId, "OFFSHORE");
        Assert.Equal(sources, before.Count(i => i.Kind == "source"));

        // Re-import ONLY the ONSHORE scope: the OFFSHORE rows of the same permit and the other permit's rows survive byte for byte.
        var replaced = await ApplyBytesAsync(app, client, permitId, small, new { region, area = "ONSHORE" });
        Assert.Equal(sources, replaced.GetProperty("replacedOwn").GetInt32());

        var after = await IdsAsync(app, client, permitId, "OFFSHORE");
        Assert.Equal(before.Select(b => b.Id).Order(), after.Select(a => a.Id).Order());
        var cardNow = await CardAsync(app, client, permitId);
        Assert.Equal(2 * sources, cardNow.GetProperty("sources").GetArrayLength());
        Assert.Equal(2 * substances, cardNow.GetProperty("substanceRows").GetInt32());
        var neighbourCard = await CardAsync(app, client, neighbour);
        Assert.Equal(sources, neighbourCard.GetProperty("sources").GetArrayLength());
        Assert.Equal(substances, neighbourCard.GetProperty("substanceRows").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Replace__set_based_delete_leaves_the_same_trace_as_deleting_entry_by_entry()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var small = Small(Synthetic.Bytes, 40);

        // Two identical permits in two regions. A is cleaned the old way (DELETE per entry, substances first), B by the import.
        var regionA = NewRegion();
        var regionB = NewRegion();
        var permitA = await NewPermitAsync(client, app);
        var permitB = await NewPermitAsync(client, app);
        await ApplyBytesAsync(app, client, permitA, small, new { region = regionA, area = "OFFSHORE", resolutions = await ResolveAsync(app, client, permitA, regionA, "OFFSHORE", small) });
        await ApplyBytesAsync(app, client, permitB, small, new { region = regionB, area = "OFFSHORE", resolutions = await ResolveAsync(app, client, permitB, regionB, "OFFSHORE", small) });
        var oldA = await IdsAsync(app, client, permitA, "OFFSHORE");
        var oldB = await IdsAsync(app, client, permitB, "OFFSHORE");
        Assert.Equal(oldA.Count, oldB.Count);

        foreach (var row in oldA.Where(r => r.Kind == "substance").Concat(oldA.Where(r => r.Kind == "source")))
        {
            var code = row.Kind == "source" ? AirLimitsSchema.SourcesRegistry : AirLimitsSchema.SubstancesRegistry;
            var gone = await client.DeleteAsync(new Uri($"/api/v1/registries/{code}/entries/{row.Id}", UriKind.Relative));
            Assert.True(gone.IsSuccessStatusCode, $"{(int)gone.StatusCode}: {await gone.Content.ReadAsStringAsync()} | {app.ErrorsText}");
        }

        await ApplyBytesAsync(app, client, permitB, small, new { region = regionB, area = "OFFSHORE" });

        await using var db = new EcrDbContext(Options());
        var a = await TraceAsync(db, oldA.Select(r => r.Id).ToList());
        var b = await TraceAsync(db, oldB.Select(r => r.Id).ToList());
        Assert.Equal(a, b);
        Assert.Equal(oldA.Count, a.Count(t => t.StartsWith("audit|", StringComparison.Ordinal)));
        Assert.Equal(oldA.Count, a.Count(t => t.StartsWith("entry|", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Apply__a_failure_after_the_deletes_and_inserts_rolls_everything_back()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql) { ExtraServices = s => s.AddScoped<IAuditWriter>(sp => new FailingImportAudit(ActivatorUtilities.CreateInstance<AuditWriter>(sp))) };
        using var client = await SignedInAsync(app, Admin);
        var small = Small(Synthetic.Bytes, 40);
        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        var resolutions = await ResolveAsync(app, client, permitId, region, "OFFSHORE", small);

        // First import fails at the very last step: not a source, a substance or a NAE node of the region may remain.
        FailingImportAudit.Armed = true;
        try
        {
            using var refused = await PostFileAsync(client, Apply(permitId), small, new { region, area = "OFFSHORE", resolutions });
            Assert.False(refused.IsSuccessStatusCode);
        }
        finally
        {
            FailingImportAudit.Armed = false;
        }

        Assert.Equal(0, (await CardAsync(app, client, permitId)).GetProperty("sources").GetArrayLength());
        var tree = await ReadAsync(app, await client.GetAsync(new Uri($"/api/v1/registries/{NaeTreeSchema.RegistryCode}/tree", UriKind.Relative)));
        Assert.DoesNotContain(tree.GetProperty("nodes").EnumerateArray(), n => (n.GetProperty("code").GetString() ?? "").Contains($"_{region}_", StringComparison.Ordinal));

        // A good import, then a replace that fails after its deletes and inserts: the first generation is still there, alive.
        await ApplyBytesAsync(app, client, permitId, small, new { region, area = "OFFSHORE", resolutions });
        var before = await IdsAsync(app, client, permitId, "OFFSHORE");
        FailingImportAudit.Armed = true;
        try
        {
            using var refused = await PostFileAsync(client, Apply(permitId), small, new { region, area = "OFFSHORE" });
            Assert.False(refused.IsSuccessStatusCode);
        }
        finally
        {
            FailingImportAudit.Armed = false;
        }

        var after = await IdsAsync(app, client, permitId, "OFFSHORE");
        Assert.Equal(before.Select(b => b.Id).Order(), after.Select(a => a.Id).Order());
        await using var db = new EcrDbContext(Options());
        var ids = before.Select(b => b.Id).ToList();
        Assert.Equal(0, await db.RegistryEntries.CountAsync(e => ids.Contains(e.Id) && e.IsDeleted));
    }

    private sealed record RowId(string Kind, long Id);

    private static async Task<List<RowId>> IdsAsync(EcrApiFactory app, HttpClient client, long permitId, string area)
    {
        var card = await CardAsync(app, client, permitId);
        var rows = new List<RowId>();
        foreach (var source in card.GetProperty("sources").EnumerateArray()
                     .Where(s => string.Equals(s.GetProperty("area").GetString(), area, StringComparison.OrdinalIgnoreCase)))
        {
            rows.Add(new RowId("source", source.GetProperty("id").GetInt64()));
            rows.AddRange(source.GetProperty("substances").EnumerateArray().Select(s => new RowId("substance", s.GetProperty("id").GetInt64())));
        }

        return rows;
    }

    /// <summary>The shape of what deleting these entries left behind, without the ids and codes that differ per permit.</summary>
    private static async Task<List<string>> TraceAsync(EcrDbContext db, List<long> ids)
    {
        var trace = new List<string>();
        foreach (var e in await db.RegistryEntries.AsNoTracking().Where(e => ids.Contains(e.Id)).ToListAsync())
        {
            trace.Add($"entry|deleted={e.IsDeleted}|by={e.DeletedByUserId}|stamped={e.DeletedAt != null}");
        }

        foreach (var k in await db.RegistryEntryKeys.AsNoTracking().Where(k => ids.Contains(k.RegistryEntryId)).ToListAsync())
        {
            trace.Add($"key|live={k.IsLive}");
        }

        var low = (int)ids.Min();
        var high = (int)ids.Max();
        var idSet = ids.Select(i => (int)i).ToHashSet();
        var audits = (await db.Database.SqlQuery<AuditShape>(
            $"SELECT EntityId, EntityType, Operation, CAST(ChangeClass AS int) AS ChangeClass, ChangeReason, ChangedByUserId, OldJson FROM aud.StructureChange WHERE EntityType = N'dic.RegistryEntry' AND Operation = N'Delete' AND EntityId BETWEEN {low} AND {high}")
            .ToListAsync()).Where(r => idSet.Contains(r.EntityId)).ToList();        foreach (var audit in audits)
        {
            var old = JsonDocument.Parse(audit.OldJson ?? "{}").RootElement;
            trace.Add($"audit|{audit.EntityType}|{audit.Operation}|{audit.ChangeClass}|{audit.ChangeReason}|{audit.ChangedByUserId}|{string.Join(",", old.EnumerateObject().Select(p => p.Name).Order())}");
        }

        trace.Sort(StringComparer.Ordinal);
        return trace;
    }

    private sealed class AuditShape
    {
        public int EntityId { get; set; }
        public string EntityType { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public int ChangeClass { get; set; }
        public string? ChangeReason { get; set; }
        public int ChangedByUserId { get; set; }
        public string? OldJson { get; set; }
    }

    /// <summary>The first <paramref name="sourceRows"/> data rows of the golden table: a small file with the same layout.</summary>
    private static byte[] Small(byte[] bytes, int sourceRows)
    {
        using var input = new MemoryStream(bytes);
        using var workbook = new ClosedXML.Excel.XLWorkbook(input);
        var sheet = workbook.Worksheets.First();
        var first = Synthetic.FirstSourceRow + sourceRows;
        var last = sheet.LastRowUsed()!.RowNumber();
        if (last >= first)
        {
            sheet.Rows(first, last).Delete();
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private static async Task<JsonElement> ApplyBytesAsync(EcrApiFactory app, HttpClient client, long permitId, byte[] bytes, object options)
        => await ReadAsync(app, await PostFileAsync(client, Apply(permitId), bytes, options));

    private static async Task<object[]> ResolveAsync(EcrApiFactory app, HttpClient client, long permitId, string region, string area, byte[] bytes)
    {
        var preview = (await ReadAsync(app, await PostFileAsync(client, Preview(permitId), bytes, new { region, area }))).GetProperty("preview");
        return [.. preview.GetProperty("missing").EnumerateArray()
            .Select(m => (object)new { kind = "nae", code = m.GetProperty("code").GetString(), action = "create", createUnder = siteNodeId.ToString(CultureInfo.InvariantCulture) })];
    }

    /// <summary>Delegates to the real audit writer, except that the final import record throws while <see cref="Armed"/>.</summary>
    private sealed class FailingImportAudit(IAuditWriter inner) : IAuditWriter
    {
        public static volatile bool Armed;

        public Task WriteCellChangesAsync(IReadOnlyList<CellChangeRecord> changes, CancellationToken ct) => inner.WriteCellChangesAsync(changes, ct);

        public Task WriteStructureChangeAsync(StructureChangeRecord change, CancellationToken ct)
            => Armed && change.Operation == "AirLimitsImport"
                ? throw new InvalidOperationException("Injected failure after the deletes and inserts.")
                : inner.WriteStructureChangeAsync(change, ct);

        public Task WriteStructureChangesAsync(IReadOnlyList<StructureChangeRecord> changes, CancellationToken ct) => inner.WriteStructureChangesAsync(changes, ct);

        public Task WriteSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct) => inner.WriteSecurityEventAsync(evt, ct);

        public Task WriteSecurityEventsAsync(IReadOnlyList<SecurityEventRecord> events, CancellationToken ct) => inner.WriteSecurityEventsAsync(events, ct);

        public Task WriteIndependentSecurityEventAsync(SecurityEventRecord evt, CancellationToken ct) => inner.WriteIndependentSecurityEventAsync(evt, ct);

        public Task WritePublicationEventAsync(PublicationEventRecord evt, CancellationToken ct) => inner.WritePublicationEventAsync(evt, ct);
    }

    // ---------- helpers ----------

    private const string Password = "Api-Air-Limits-2026!";

    private DbContextOptions<EcrDbContext> Options() => new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options;

    private async Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
    {
        var name = $"air_{Guid.NewGuid():N}"[..20];
        await using (var db = new EcrDbContext(Options()))
        {
            var user = new User(name, name, AuthProvider.Local);
            user.SetPassword(new PasswordHasher().Hash(Password));
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var role = new Role(EcrCode.Create($"R{Guid.NewGuid():N}"[..12]), new LocalizedText(new Dictionary<string, string> { ["en"] = "Air limits test" }));
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
        client.Timeout = TimeSpan.FromMinutes(15);
        var login = await client.PostAsJsonAsync(new Uri("/api/v1/login/local", UriKind.Relative), new { userName = name, password = Password });
        Assert.True(login.IsSuccessStatusCode, $"{login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private const string Substances = "LAND_AIR_SUBSTANCE";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static decimal Dec(JsonElement e)
        => e.ValueKind == JsonValueKind.String ? decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture) : e.GetDecimal();

    private static string Preview(long permitId) => $"/api/v1/permits/{permitId}/air-limits/import/preview";

    private static string Apply(long permitId) => $"/api/v1/permits/{permitId}/air-limits/import/apply";

    private static string NewRegion() => $"T{Guid.NewGuid():N}"[..10].ToUpperInvariant();

    private static byte[] Edit(byte[] bytes, string column, int row, string? value)
    {
        using var input = new MemoryStream(bytes);
        using var workbook = new ClosedXML.Excel.XLWorkbook(input);
        var cell = workbook.Worksheets.First().Cell($"{column}{row}");
        if (value is null)
        {
            cell.Clear();
        }
        else
        {
            cell.Value = value;
        }

        using var output = new MemoryStream();
        workbook.SaveAs(output);
        return output.ToArray();
    }

    private static async Task<HttpResponseMessage> PostFileAsync(HttpClient client, string url, byte[] bytes, object request)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "table33.xlsx");
        content.Add(new StringContent(JsonSerializer.Serialize(request, Web)), "request");
        return await client.PostAsync(new Uri(url, UriKind.Relative), content);
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

    private static async Task AssertErrorAsync(EcrApiFactory app, HttpResponseMessage response, HttpStatusCode status, string messageKey)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text} | {app.ErrorsText}");
            Assert.Contains(messageKey, text, StringComparison.Ordinal);
        }
    }

    private static async Task<JsonElement> CardAsync(EcrApiFactory app, HttpClient client, long permitId)
        => await ReadAsync(app, await client.GetAsync(new Uri($"/api/v1/permits/{permitId}/air-limits", UriKind.Relative)));

    private static async Task<JsonElement> ApplyAsync(EcrApiFactory app, HttpClient client, long permitId, object options)
        => await ReadAsync(app, await PostFileAsync(client, Apply(permitId), Synthetic.Bytes, options));

    private static async Task<object[]> ResolveAllAsync(EcrApiFactory app, HttpClient client, long permitId, string region)
    {
        var preview = (await ReadAsync(app, await PostFileAsync(client, Preview(permitId), Synthetic.Bytes, new { region, area = "OFFSHORE" }))).GetProperty("preview");
        return [.. preview.GetProperty("missing").EnumerateArray()
            .Select(m => (object)new { kind = "nae", code = m.GetProperty("code").GetString(), action = "create", createUnder = siteNodeId.ToString(CultureInfo.InvariantCulture) })];
    }

    private static async Task<long> NewPermitAsync(HttpClient client, EcrApiFactory app)
    {
        var number = $"AIR-{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var created = await client.PostAsJsonAsync(
            new Uri("/api/v1/permits", UriKind.Relative),
            new { typeCode = "AIR_LIMITS", number, location = (string?)null, issueDate = (string?)null, validFrom = "2026-01-01", validTo = "2031-01-01", reportingFlags = (object?)null });
        return (await ReadAsync(app, created)).GetProperty("id").GetInt64();
    }

    /// <summary>
    /// One-time per test run: the substance registry with the 55 synthetic codes, the NAE tree with the site node the new NAE are
    /// created under, and the provisioned permit type.
    /// </summary>
    private async Task PrepareAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (prepared)
            {
                return;
            }

            var fake = new FakeAf();
            using var app = new EcrApiFactory(sql) { ExtraServices = s => s.AddSingleton<INaeAfReader>(fake) };
            using var client = await SignedInAsync(app, Admin);

            // Substance registry: one entry E<code> per synthetic substance, SUBSTANCE_CODE without the zero padding.
            if ((await client.GetAsync(new Uri($"/api/v1/registries/{Substances}/definition", UriKind.Relative))).StatusCode == HttpStatusCode.NotFound)
            {
                await ReadAsync(app, await client.PostAsJsonAsync(
                    new Uri("/api/v1/registries", UriKind.Relative),
                    new { code = Substances, nameL10n = new Dictionary<string, string> { ["en"] = "Air substances" }, isTemporal = false }));
                await ReadAsync(app, await client.PutDefinitionAsync(
                    new Uri($"/api/v1/registries/{Substances}/definition", UriKind.Relative),
                    new
                    {
                        fields = new object[]
                        {
                            new
                            {
                                id = (int?)null, code = "SUBSTANCE_CODE", nameL10n = new { values = new Dictionary<string, string> { ["en"] = "Code" } },
                                dataType = "String", ordinal = 1, isRequired = true, isKey = true, lookupRegistryDefId = (int?)null, unitId = (int?)null,
                            },
                        },
                        rules = Array.Empty<object>(),
                        reason = "NAE N3 test registry",
                    }));
                var items = Synthetic.Model.SelectMany(m => m.Substances).Select(s => s.Code).Distinct()
                    .Select(c => new
                    {
                        clientRowId = c,
                        op = "upsert",
                        id = (long?)null,
                        code = $"E{int.Parse(c, CultureInfo.InvariantCulture)}",
                        baseVersion = (string?)null,
                        values = new Dictionary<string, object?> { ["SUBSTANCE_CODE"] = int.Parse(c, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture) },
                    })
                    .ToArray();
                Assert.Equal(55, items.Length);
                var batch = await client.PostAsJsonAsync(new Uri($"/api/v1/registries/{Substances}/entries/batch?dryRun=false", UriKind.Relative), new { items });
                var batchText = await batch.Content.ReadAsStringAsync();
                Assert.True(batch.IsSuccessStatusCode && batchText.Contains("\"applied\":true", StringComparison.Ordinal), batchText);
            }

            // NAE tree with the standard AF snapshot; the site node is where the golden test creates its NAE.
            await ReadAsync(app, await client.PostAsJsonAsync(
                new Uri("/api/v1/nae-tree/af-import", UriKind.Relative),
                new { dataSourceId = 1, rootPath = FakeAf.Root, registryCode = NaeTreeSchema.RegistryCode, dryRun = false }));
            var tree = await ReadAsync(app, await client.GetAsync(new Uri($"/api/v1/registries/{NaeTreeSchema.RegistryCode}/tree", UriKind.Relative)));
            siteNodeId = tree.GetProperty("nodes").EnumerateArray()
                .First(n => n.GetProperty("levelCode").GetString() == "SITE").GetProperty("id").GetInt64();

            await ReadAsync(app, await client.PostAsync(new Uri("/api/v1/air-limits/provision", UriKind.Relative), null));
            prepared = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private sealed class FakeAf : INaeAfReader
    {
        public const string Root = @"\\AF\ECR\ECR_01_Air Equipment";

        public Task<NaeAfSnapshot> ReadAsync(int dataSourceId, string rootPath, CancellationToken ct)
            => Task.FromResult(new NaeAfSnapshot(
            [
                new NaeAfElement($@"{Root}\Atyrau_Air", "Atyrau_Air", null, null, null, null, null),
                new NaeAfElement($@"{Root}\Atyrau_Air\Offshore\Island_A", "Island_A", null, null, null, null, null),
                new NaeAfElement($@"{Root}\Atyrau_Air\Offshore\Island_A\9001", "9001", "Seed NAE", null, null, "Atyrau", "Offshore"),
            ], true));
    }
}
