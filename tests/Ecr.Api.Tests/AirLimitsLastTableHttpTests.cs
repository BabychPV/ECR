using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Registries.Nae;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ✎ D-569 (P-1123-1, людина 23.11): «кастомні колонки які я додаю, в моєму прикладі Цех, не додаються … загальне правило одна остання
/// завантажена таблиця актуальна». Through the real endpoints and the 401 test file's layout (<see cref="Air401Shape"/>):
/// «Цех» added in the wizard is imported (red before the fix: the column stayed empty), an unread added column blocks the import,
/// the last table replaces every row of the permit in every area with no stale row, no duplicate and no carry-over, and the
/// preview lists exactly what it replaces. Mutation proofs are run locally and never committed (CLAUDE.md, 01.10).
/// </summary>
public sealed partial class AirLimitsImportHttpTests
{
    private static string[] ShapeCodes => [.. Synthetic.Model.SelectMany(m => m.Substances).Select(s => s.Code).Distinct().Take(3)];

    private static Air401Shape.Substance ShapeSub(int index, decimal tons) => new(ShapeCodes[index], $"Substance {index}", 0.0002m, 1.263m, tons);

    private static async Task<HttpResponseMessage> PostNamedAsync(HttpClient client, string url, byte[] bytes, object request, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", fileName);
        content.Add(new StringContent(JsonSerializer.Serialize(request, Web)), "request");
        return await client.PostAsync(new Uri(url, UriKind.Relative), content);
    }

    /// <summary>The built-in profile plus ONE field the user adds in the wizard's «Add field»: a name, level «Source», nothing else.</summary>
    private static JsonElement SpecWithWizardField(string name)
    {
        var era = Ecr.Application.RegistryImport.BuiltInImportProfiles.EraTable33;
        return JsonDocument.Parse(era.WithFields(
            [.. era.Fields, new Ecr.Application.RegistryImport.ImportFieldSpec { Name = name, Level = Ecr.Application.RegistryImport.ImportLevel.Parent }],
            []).ToJson()).RootElement.Clone();
    }

    /// <remarks>
    /// The reproduced live case (scratch copy of live 23.11, a9a3c0067): the added column «Цех» (<c>FILE_B</c>) and a field «Цех»
    /// added in the wizard with no file column and no header text. <c>BindFields</c> bound it to the column, the parser found no
    /// column for it (a warning) and, the column being taken by the field, the header-name fallback was not offered: the apply
    /// went through and the column stayed empty. Red without the fix (run locally): the card has no value for the column.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Tsekh__a_field_added_in_the_wizard_without_column_or_header_text_reads_the_merged_header_of_the_401_file()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var stamp = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var code = $"FILE_B_{stamp}";
        var title = $"Цех {stamp}";
        await AddColumnAsync(app, client, AirLimitsSchema.SourcesTable, code, "String", title);
        var bytes = Air401Shape.Build(
            [new("0003", "еуіе", "UNIT TEST-001A", 9.5m, [ShapeSub(0, 123m)])],
            workshopHeader: title);
        var spec = SpecWithWizardField(title);

        // The analysis (user's names) already reads «Цех» from B — no finding about it.
        var analysis = await ReadAsync(app, await PostFileAsync(client, "/api/v1/air-limits/import/analyse", bytes, new { spec }));
        Assert.True(analysis.GetProperty("analysis").GetProperty("canContinue").GetBoolean(), analysis.GetRawText());
        Assert.Contains(
            analysis.GetProperty("analysis").GetProperty("header").GetProperty("mapped").EnumerateArray(),
            m => m.GetProperty("field").GetString() == title && m.GetProperty("columnLetter").GetString() == "B");

        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        var resolutions = await ResolveAsync(app, client, permitId, region, "OFFSHORE", bytes);
        var applied = await ReadAsync(app, await PostNamedAsync(
            client, Apply(permitId), bytes, new { region, area = "OFFSHORE", resolutions, spec }, "7) Параметры_2024_final_Test.xlsx"));
        Assert.Equal(1, applied.GetProperty("sources").GetInt32());

        var card = await CardAsync(app, client, permitId);
        var source = Assert.Single(card.GetProperty("sources").EnumerateArray());
        Assert.Equal("еуіе", source.GetProperty("added").TryGetProperty(code, out var v) ? v.GetString() : null);
        var current = card.GetProperty("currentTable");
        Assert.Equal("7) Параметры_2024_final_Test.xlsx", current.GetProperty("fileName").GetString());
        Assert.False(string.IsNullOrEmpty(current.GetProperty("importedBy").GetString()));
    }

    /// <remarks>
    /// The wizard's «Add field» starts at the level «Substance row». A field named after a SOURCE column («Цех») at that level used
    /// to be reported «values will not be saved» (and read nothing). Now it is bound to the source column and read at its level.
    /// Red without the fix (run locally): <c>unsavedFields</c> lists the field.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Tsekh__a_field_added_at_the_default_level_substance_row_is_the_source_column_not_unsaved()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var stamp = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var code = $"FILE_B_{stamp}";
        var title = $"Цех {stamp}";
        await AddColumnAsync(app, client, AirLimitsSchema.SourcesTable, code, "String", title);
        var bytes = Air401Shape.Build(
            [new("0003", "WS-A", "UNIT A", 9.5m, [ShapeSub(0, 1m), ShapeSub(1, 2m)]), new("0004", "WS-B", "UNIT B", 9.5m, [ShapeSub(2, 3m)])],
            workshopHeader: title);
        var era = Ecr.Application.RegistryImport.BuiltInImportProfiles.EraTable33;
        var spec = JsonDocument.Parse(era.WithFields(
            [.. era.Fields, new Ecr.Application.RegistryImport.ImportFieldSpec { Name = title, Level = Ecr.Application.RegistryImport.ImportLevel.Child }],
            []).ToJson()).RootElement.Clone();

        var analysis = await ReadAsync(app, await PostFileAsync(client, "/api/v1/air-limits/import/analyse", bytes, new { spec }));
        Assert.Empty(analysis.GetProperty("unsavedFields").EnumerateArray());
        Assert.True(analysis.GetProperty("analysis").GetProperty("canContinue").GetBoolean(), analysis.GetRawText());

        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        var resolutions = await ResolveAsync(app, client, permitId, region, "OFFSHORE", bytes);
        var preview = await ReadAsync(app, await PostFileAsync(client, Preview(permitId), bytes, new { region, area = "OFFSHORE", resolutions, spec }));
        Assert.Empty(preview.GetProperty("unsavedFields").EnumerateArray());
        await ApplyBytesAsync(app, client, permitId, bytes, new { region, area = "OFFSHORE", resolutions, spec });

        var sources = (await CardAsync(app, client, permitId)).GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(["WS-A", "WS-B"], sources.Select(s => s.GetProperty("added").GetProperty(code).GetString()).Order());
    }

    /// <remarks>
    /// A permit imported before the per-permit «current table» record existed (live: every import up to D-569) still names when and
    /// who from the import's record on the sources table. Red without the fallback: <c>currentTable</c> is null.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task CurrentTable__a_permit_imported_before_the_permit_record_still_names_its_last_import()
    {
        await PrepareAsync();
        // As before D-569: the import writes no per-permit record (the audit journal is immutable, so it is never written here).
        using var app = new EcrApiFactory(sql)
        {
            ExtraServices = s => s.AddScoped<Ecr.Application.Ports.IAuditWriter>(sp => new WithoutCurrentTableAudit(
                Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Ecr.Infrastructure.Persistence.AuditWriter>(sp))),
        };
        using var client = await SignedInAsync(app, Admin);
        var bytes = Air401Shape.Build([new("0003", null, "UNIT A", 9.5m, [ShapeSub(0, 1m)])]);
        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        await ReadAsync(app, await PostNamedAsync(client, Apply(permitId), bytes,
            new { region, area = "OFFSHORE", resolutions = await ResolveAsync(app, client, permitId, region, "OFFSHORE", bytes) }, "earlier.xlsx"));

        await using (var db = new EcrDbContext(Options()))
        {
            var mine = await db.Database.SqlQuery<int>(
                $"SELECT COUNT(*) AS Value FROM aud.StructureChange WHERE EntityType = {AirLimitsSchema.CurrentTableAuditEntity} AND EntityId = {(int)permitId}").SingleAsync();
            Assert.Equal(0, mine);
        }

        var current = (await CardAsync(app, client, permitId)).GetProperty("currentTable");
        Assert.Equal(JsonValueKind.Object, current.ValueKind);
        Assert.Equal("earlier.xlsx", current.GetProperty("fileName").GetString());
        Assert.False(string.IsNullOrEmpty(current.GetProperty("importedBy").GetString()));
        Assert.Equal(1, current.GetProperty("sources").GetInt32());
    }

    /// <remarks>
    /// An added column the specification says to read but that ends up unread is a BLOCKING error with a clear message — the file
    /// has no header named like the column (and the field names no column), or the field's column is read by another field.
    /// Red without the fix: a warning only, <c>canApply</c> true and the apply succeeds with the column empty.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Tsekh__an_added_column_the_specification_reads_but_cannot_read_blocks_the_import()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var stamp = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var title = $"Цех {stamp}";
        await AddColumnAsync(app, client, AirLimitsSchema.SourcesTable, $"FILE_B_{stamp}", "String", title);
        // The file's header B says «Цех», not the column's name: the wizard field named after the column finds no column.
        var bytes = Air401Shape.Build([new("0003", "еуіе", "UNIT TEST-001A", 9.5m, [ShapeSub(0, 123m)])]);

        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        var resolutions = await ResolveAsync(app, client, permitId, region, "OFFSHORE", bytes);
        foreach (var spec in new[]
                 {
                     SpecWithWizardField(title),
                     // The same field pointed at column C, which «Source name» reads.
                     JsonDocument.Parse(Ecr.Application.RegistryImport.BuiltInImportProfiles.EraTable33.WithFields(
                         [.. Ecr.Application.RegistryImport.BuiltInImportProfiles.EraTable33.Fields,
                          new Ecr.Application.RegistryImport.ImportFieldSpec { Name = title, Level = Ecr.Application.RegistryImport.ImportLevel.Parent, Column = "C" }],
                         []).ToJson()).RootElement.Clone(),
                 })
        {
            var analysis = await ReadAsync(app, await PostFileAsync(client, "/api/v1/air-limits/import/analyse", bytes, new { spec }));
            Assert.False(analysis.GetProperty("analysis").GetProperty("canContinue").GetBoolean());

            var preview = (await ReadAsync(app, await PostFileAsync(client, Preview(permitId), bytes, new { region, area = "OFFSHORE", resolutions, spec }))).GetProperty("preview");
            Assert.False(preview.GetProperty("canApply").GetBoolean());
            var issue = Assert.Single(preview.GetProperty("issues").EnumerateArray(), i => i.GetProperty("messageKey").GetString() == AirLimitsKeys.AddedColumnUnread);
            Assert.Equal("Error", issue.GetProperty("severity").GetString());
            Assert.Equal(title, issue.GetProperty("args").GetProperty("column").GetString());

            await AssertErrorAsync(app, await PostFileAsync(client, Apply(permitId), bytes, new { region, area = "OFFSHORE", resolutions, spec }),
                HttpStatusCode.UnprocessableEntity, "regRejected");
        }

        Assert.Equal(0, (await CardAsync(app, client, permitId)).GetProperty("sources").GetArrayLength());
    }

    /// <remarks>
    /// Two tables with DIFFERENT NAE for two areas of the same permit (both areas in the NAE tree, as Atyrau on live): the second
    /// import leaves only its own rows — the first area's rows are removed (soft-deleted), the preview lists them as removed with
    /// their area, and another permit's rows are untouched. Red without the fix (run locally: own rows filtered by
    /// <c>air.Holds</c> again): the card keeps the OFFSHORE rows next to the new ONSHORE ones.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task LastTable__a_second_file_with_other_NAE_in_another_area_leaves_only_its_rows()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        var neighbour = await NewPermitAsync(client, app);
        await using (var db = new EcrDbContext(Options()))
        {
            var defId = (await db.RegistryDefs.AsNoTracking().SingleAsync(d => d.Code == NaeTreeSchema.RegistryCode)).Id;
            async Task<long> NodeAsync(string code, string level, long? parent)
                => (await ReadAsync(app, await client.PostAsJsonAsync(
                    new Uri($"/api/v1/registries/{NaeTreeSchema.RegistryCode}/entries", UriKind.Relative),
                    new { id = (long?)null, registryDefId = defId, code, display = new { values = new Dictionary<string, string> { ["en"] = code } }, parentEntryId = parent, levelCode = level, values = new Dictionary<string, object?>() })))
                    .GetProperty("id").GetInt64();
            var regionNode = await NodeAsync($"R_{region}", NaeTreeSchema.LevelRegion, null);
            await NodeAsync($"A_{region}_OFFSHORE", NaeTreeSchema.LevelArea, regionNode);
            await NodeAsync($"A_{region}_ONSHORE", NaeTreeSchema.LevelArea, regionNode);
        }

        var first = Air401Shape.Build(
        [
            new("9101", "WS-1", "Offshore one", 10m, [ShapeSub(0, 1m), ShapeSub(1, 2m)]),
            new("9102", "WS-2", "Offshore two", 11m, [ShapeSub(0, 3m)]),
            new("9103", null, "Offshore three", 12m, [ShapeSub(2, 4m)]),
        ]);
        var second = Air401Shape.Build(
        [
            new("9201", "WS-9", "Onshore one", 20m, [ShapeSub(0, 5m)]),
            new("9202", null, "Onshore two", 21m, [ShapeSub(1, 6m), ShapeSub(2, 7m)]),
        ]);

        await ReadAsync(app, await PostNamedAsync(client, Apply(permitId), first,
            new { region, area = "OFFSHORE", resolutions = await ResolveAsync(app, client, permitId, region, "OFFSHORE", first) }, "offshore.xlsx"));
        var otherRegion = NewRegion();
        await ApplyBytesAsync(app, client, neighbour, first,
            new { region = otherRegion, area = "OFFSHORE", resolutions = await ResolveAsync(app, client, neighbour, otherRegion, "OFFSHORE", first) });
        var before = await IdsAsync(app, client, permitId, "OFFSHORE");
        Assert.Equal(3, before.Count(i => i.Kind == "source"));

        var resolutions = await ResolveAsync(app, client, permitId, region, "ONSHORE", second);
        var preview = (await ReadAsync(app, await PostFileAsync(client, Preview(permitId), second, new { region, area = "ONSHORE", resolutions }))).GetProperty("preview");
        Assert.True(preview.GetProperty("canApply").GetBoolean());
        Assert.False(preview.GetProperty("replace").GetProperty("confirmationRequired").GetBoolean());
        Assert.Equal(3, preview.GetProperty("replace").GetProperty("thisOwner").GetProperty("parents").GetInt32());
        var diff = preview.GetProperty("diff");
        Assert.Equal((2, 0, 3), (diff.GetProperty("newParents").GetInt32(), diff.GetProperty("unchangedParents").GetInt32(), diff.GetProperty("removedParents").GetInt32()));
        Assert.Equal((3, 4), (diff.GetProperty("newChildren").GetInt32(), diff.GetProperty("removedChildren").GetInt32()));
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(["9101", "9102", "9103"], rows.Where(r => r.GetProperty("kind").GetString() == "Removed" && r.GetProperty("childKey").ValueKind == JsonValueKind.Null)
            .Select(r => r.GetProperty("key").GetString()).Order());
        Assert.All(rows.Where(r => r.GetProperty("kind").GetString() == "Removed"), r => Assert.Contains("OFFSHORE", r.GetProperty("scope").GetString(), StringComparison.Ordinal));
        Assert.Equal(["9201", "9202"], rows.Where(r => r.GetProperty("kind").GetString() == "Added" && r.GetProperty("childKey").ValueKind == JsonValueKind.Null)
            .Select(r => r.GetProperty("key").GetString()).Order());

        var done = await ReadAsync(app, await PostNamedAsync(client, Apply(permitId), second,
            new { region, area = "ONSHORE", resolutions, expectedFingerprint = preview.GetProperty("fingerprint").GetString() }, "onshore.xlsx"));
        Assert.Equal(3, done.GetProperty("replacedOwn").GetInt32());

        var card = await CardAsync(app, client, permitId);
        var sources = card.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(["9201", "9202"], sources.Select(s => s.GetProperty("naeCode").GetString()).Order());
        Assert.All(sources, s => Assert.Equal("ONSHORE", s.GetProperty("area").GetString()));
        Assert.Equal(3, card.GetProperty("substanceRows").GetInt32());
        Assert.Equal("onshore.xlsx", card.GetProperty("currentTable").GetProperty("fileName").GetString());

        await using (var db = new EcrDbContext(Options()))
        {
            var ids = before.Select(b => b.Id).ToList();
            Assert.Equal(ids.Count, await db.RegistryEntries.CountAsync(e => ids.Contains(e.Id) && e.IsDeleted));
            var audit = await db.Database.SqlQuery<string>(
                $"SELECT TOP 1 OldJson AS Value FROM aud.StructureChange WHERE EntityType = {AirLimitsSchema.CurrentTableAuditEntity} AND EntityId = {(int)permitId} ORDER BY ChangedAt DESC, Id DESC").SingleAsync();
            using var old = JsonDocument.Parse(audit);
            Assert.Equal(3, old.RootElement.GetProperty("removed").GetArrayLength());
        }

        var theirs = await CardAsync(app, client, neighbour);
        Assert.Equal(3, theirs.GetProperty("sources").GetArrayLength());
    }

    /// <remarks>
    /// A re-import in the SAME area with removed, added and changed rows: no stale row and no duplicate remain, nothing is carried
    /// over — «Цех» empty in the new file is empty after it, and an added substance column the new specification does not read is
    /// emptied (the preview names it in <c>emptiedColumns</c> and lists «field: old → empty»). Red without the fix (run locally,
    /// the F13-p198 carry-over put back in <c>InsertAsync</c>): the old values survive.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task LastTable__a_reimport_with_removed_added_and_changed_rows_leaves_no_stale_row_no_duplicate_and_no_carry_over()
    {
        await PrepareAsync();
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, Admin);
        var stamp = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var workshop = $"FILE_B_{stamp}";
        var year = $"NDV_{stamp}";
        var title = $"Цех {stamp}";
        await AddColumnAsync(app, client, AirLimitsSchema.SourcesTable, workshop, "String", title);
        await AddColumnAsync(app, client, AirLimitsSchema.SubstancesTable, year, "Int");
        var era = Ecr.Application.RegistryImport.BuiltInImportProfiles.EraTable33;
        var withYear = JsonDocument.Parse(era.WithFields(
            [.. era.Fields, new Ecr.Application.RegistryImport.ImportFieldSpec
            {
                Name = year, Level = Ecr.Application.RegistryImport.ImportLevel.Child, Column = "AB", Parser = Ecr.Application.RegistryImport.ImportParser.Integer,
            }],
            []).ToJson()).RootElement.Clone();

        var first = Air401Shape.Build(
        [
            new("9301", "WS-1", "Source one", 10m, [ShapeSub(0, 1m), ShapeSub(1, 2m)]),
            new("9302", "WS-2", "Source two", 11m, [ShapeSub(0, 3m)]),
            new("9303", "WS-3", "Source three", 12m, [ShapeSub(2, 4m)]),
        ], workshopHeader: title);
        var second = Air401Shape.Build(
        [
            // 9301: «Цех» now empty, substance 1 removed, substance 2 added, substance 0 with a new t/year.
            new("9301", null, "Source one", 10m, [ShapeSub(0, 1.5m), ShapeSub(2, 9m)]),
            new("9304", "WS-4", "Source four", 14m, [ShapeSub(1, 8m)]),
        ], workshopHeader: title);

        var region = NewRegion();
        var permitId = await NewPermitAsync(client, app);
        var resolutions = await ResolveAsync(app, client, permitId, region, "OFFSHORE", first);
        await ApplyBytesAsync(app, client, permitId, first, new { region, area = "OFFSHORE", resolutions, spec = withYear });
        var stored = await CardAsync(app, client, permitId);
        Assert.Equal("WS-1", stored.GetProperty("sources").EnumerateArray().Single(s => s.GetProperty("naeCode").GetString() == "9301").GetProperty("added").GetProperty(workshop).GetString());

        // The second table with the built-in profile: «Цех» is read by its header name, the year column is not read at all.
        var secondResolutions = await ResolveAsync(app, client, permitId, region, "OFFSHORE", second);
        var answer = await ReadAsync(app, await PostFileAsync(client, Preview(permitId), second, new { region, area = "OFFSHORE", resolutions = secondResolutions }));
        Assert.Contains(answer.GetProperty("emptiedColumns").EnumerateArray(), c => c.GetProperty("code").GetString() == year);
        Assert.DoesNotContain(answer.GetProperty("emptiedColumns").EnumerateArray(), c => c.GetProperty("code").GetString() == workshop);
        var preview = answer.GetProperty("preview");
        var diff = preview.GetProperty("diff");
        Assert.Equal((1, 1, 2), (diff.GetProperty("newParents").GetInt32(), diff.GetProperty("changedParents").GetInt32(), diff.GetProperty("removedParents").GetInt32()));
        var rows = preview.GetProperty("rows").EnumerateArray().ToList();
        JsonElement Row(string kind, string key, string? child)
            => Assert.Single(rows, r => r.GetProperty("kind").GetString() == kind && r.GetProperty("key").GetString() == key
                                       && (child is null ? r.GetProperty("childKey").ValueKind == JsonValueKind.Null : r.GetProperty("childKey").GetString() == child));
        var parentChange = Row("Changed", "9301", null);
        var workshopChange = Assert.Single(parentChange.GetProperty("fields").EnumerateArray(), f => f.GetProperty("field").GetString() == workshop);
        Assert.Equal(("WS-1", JsonValueKind.Null), (workshopChange.GetProperty("old").GetString(), workshopChange.GetProperty("new").ValueKind));
        var tons = Row("Changed", "9301", ShapeCodes[0]).GetProperty("fields").EnumerateArray().ToList();
        Assert.Contains(tons, f => f.GetProperty("field").GetString() == "tonsPerYear" && f.GetProperty("old").GetString() == "1" && f.GetProperty("new").GetString() == "1.5");
        Assert.Contains(tons, f => f.GetProperty("field").GetString() == year && f.GetProperty("old").GetString() == "2024" && f.GetProperty("new").ValueKind == JsonValueKind.Null);
        Row("Removed", "9301", ShapeCodes[1]);
        Row("Added", "9301", ShapeCodes[2]);
        Row("Added", "9304", null);
        Row("Removed", "9302", null);
        Row("Removed", "9303", null);

        await ApplyBytesAsync(app, client, permitId, second, new { region, area = "OFFSHORE", resolutions = secondResolutions });

        var card = await CardAsync(app, client, permitId);
        var sources = card.GetProperty("sources").EnumerateArray().ToList();
        Assert.Equal(["9301", "9304"], sources.Select(s => s.GetProperty("naeCode").GetString()).Order());
        var one = sources.Single(s => s.GetProperty("naeCode").GetString() == "9301");
        Assert.False(one.GetProperty("added").TryGetProperty(workshop, out _), "«Цех» empty in the last table is empty (no carry-over)");
        Assert.Equal("WS-4", sources.Single(s => s.GetProperty("naeCode").GetString() == "9304").GetProperty("added").GetProperty(workshop).GetString());
        var substances = one.GetProperty("substances").EnumerateArray().ToList();
        Assert.Equal([ShapeCodes[0], ShapeCodes[2]], substances.Select(s => s.GetProperty("substanceCode").GetString()).Order());
        Assert.All(sources.SelectMany(s => s.GetProperty("substances").EnumerateArray()), s => Assert.False(s.GetProperty("added").TryGetProperty(year, out _)));
        Assert.Equal(1.5m, Dec(substances.Single(s => s.GetProperty("substanceCode").GetString() == ShapeCodes[0]).GetProperty("tonsPerYear")));

        // No stale or duplicate rows behind the card: the live rows of the permit are exactly these.
        await using var db = new EcrDbContext(Options());
        var sourcesDef = await db.RegistryDefs.AsNoTracking().SingleAsync(d => d.Code == AirLimitsSchema.SourcesRegistry);
        var permitField = await db.RegistryFieldDefs.AsNoTracking().SingleAsync(f => f.RegistryDefId == sourcesDef.Id && f.Code == Ecr.Application.Permits.PermitHeaderFields.PermitFieldCode);
        var live = await db.RegistryValues.AsNoTracking()
            .Where(v => v.RegistryFieldDefId == permitField.Id && v.ValueRefEntryId == permitId)
            .Join(db.RegistryEntries.Where(e => !e.IsDeleted), v => v.RegistryEntryId, e => e.Id, (v, e) => e.Id)
            .CountAsync();
        Assert.Equal(2, live);
    }

    /// <summary>The real audit writer minus the per-permit «current table» record: an import as it was before D-569.</summary>
    private sealed class WithoutCurrentTableAudit(Ecr.Application.Ports.IAuditWriter inner) : Ecr.Application.Ports.IAuditWriter
    {
        public Task WriteCellChangesAsync(IReadOnlyList<Ecr.Application.Ports.CellChangeRecord> changes, CancellationToken ct) => inner.WriteCellChangesAsync(changes, ct);

        public Task WriteStructureChangeAsync(Ecr.Application.Ports.StructureChangeRecord change, CancellationToken ct)
            => change.EntityType == AirLimitsSchema.CurrentTableAuditEntity ? Task.CompletedTask : inner.WriteStructureChangeAsync(change, ct);

        public Task WriteStructureChangesAsync(IReadOnlyList<Ecr.Application.Ports.StructureChangeRecord> changes, CancellationToken ct) => inner.WriteStructureChangesAsync(changes, ct);

        public Task WriteSecurityEventAsync(Ecr.Application.Ports.SecurityEventRecord evt, CancellationToken ct) => inner.WriteSecurityEventAsync(evt, ct);

        public Task WriteSecurityEventsAsync(IReadOnlyList<Ecr.Application.Ports.SecurityEventRecord> events, CancellationToken ct) => inner.WriteSecurityEventsAsync(events, ct);

        public Task WriteIndependentSecurityEventAsync(Ecr.Application.Ports.SecurityEventRecord evt, CancellationToken ct) => inner.WriteIndependentSecurityEventAsync(evt, ct);

        public Task WritePublicationEventAsync(Ecr.Application.Ports.PublicationEventRecord evt, CancellationToken ct) => inner.WritePublicationEventAsync(evt, ct);
    }
}