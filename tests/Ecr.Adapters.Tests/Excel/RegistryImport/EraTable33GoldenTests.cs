using Ecr.Adapters.Excel.RegistryImport;
using Ecr.Application.Errors;
using Ecr.Application.RegistryImport;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests.Excel.RegistryImport;

/// <summary>
/// Golden tests of the generic import engine on a workbook shaped like the ERA table 3.3 form: 373 sources, 2,624 substance rows,
/// 55 substances, 27 group rows. The workbook is synthetic (see <see cref="SyntheticEraTable33"/>); the real file is checked
/// only when <c>ECR_NAE_SAMPLE_XLSX</c> points to it, and is never committed.
/// </summary>
public sealed class EraTable33GoldenTests
{
    private static readonly RegistryImportSpec Era = BuiltInImportProfiles.EraTable33;
    private static readonly SyntheticEraTable33.Workbook Synthetic = SyntheticEraTable33.Build();

    private static ParsedImport Parse(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var workbook = new ClosedXML.Excel.XLWorkbook(stream);
        return RegistryImportParser.Parse(new ClosedXmlImportGrid(workbook.Worksheets.First()), Era);
    }

    [Fact]
    public void The_file_has_the_counts_of_the_real_form()
    {
        Assert.Equal(373, SyntheticEraTable33.Sources);
        var parsed = Parse(Synthetic.Bytes);

        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Issues.Take(5).Select(i => $"{i.MessageKey} r{i.Row} {i.Field}")));
        // The only finding is the one deliberately empty t/year cell: a per-row WARNING (not an error, not silent).
        var warning = Assert.Single(parsed.Issues);
        Assert.Equal(ImportSeverity.Warning, warning.Severity);
        Assert.Equal(RegistryImportKeys.ValueEmpty, warning.MessageKey);
        Assert.Equal(("tonsPerYear", "AA"), (warning.Field, warning.Column));
        Assert.NotNull(warning.Row);
        Assert.Equal(373, parsed.Stats.Parents);
        Assert.Equal(2624, parsed.Stats.Children);
        Assert.Equal(55, parsed.Stats.DistinctChildKeys);
        Assert.Equal(27, parsed.Stats.GroupRows);
        Assert.Equal(5, parsed.Stats.SkippedRows);      // TOTAL + four blocks of formulas
        Assert.Equal(373, parsed.Parents.Select(p => p.Key).Distinct().Count());
    }

    [Fact]
    public void Header_rows_four_to_six_and_data_from_row_eight()
    {
        var header = Parse(Synthetic.Bytes).Header!;

        Assert.Equal((4, 6, 8), (header.HeaderFromRow, header.HeaderToRow, header.DataFromRow));
        Assert.Equal(
            ["B", "R", "S", "T", "U", "V", "AB"],
            header.Unmapped.OrderBy(u => u.Column).Select(u => u.ColumnLetter));
        Assert.Equal(["AB"], header.Unmapped.Where(u => u.HasData).Select(u => u.ColumnLetter));
    }

    [Fact]
    public void Every_source_and_substance_equals_the_model_it_was_generated_from()
    {
        var parsed = Parse(Synthetic.Bytes);

        Assert.Equal(Synthetic.Model.Count, parsed.Parents.Count);

        for (var i = 0; i < Synthetic.Model.Count; i++)
        {
            var m = Synthetic.Model[i];
            var p = parsed.Parents[i];

            Assert.Equal(m.Nae, p.Key);
            Assert.Equal(m.Name, p.Values["sourceName"]);
            Assert.Equal(m.Quantity, p.Values["quantity"]);
            Assert.Equal(m.Hours, p.Values["hoursPerYear"]);
            Assert.Equal(m.PointName, p.Values["emissionPointName"]);
            Assert.Equal(m.Type, p.Values["sourceType"]);
            Assert.Equal(m.Height, p.Values["height"]);
            Assert.Equal(m.Diameter, p.Values["diameter"]);
            Assert.Equal(m.Velocity, p.Values["velocity"]);
            Assert.Equal(m.Volume, p.Values["volume"]);
            Assert.Equal(m.Temperature, p.Values["temperature"]);
            Assert.Equal(m.X1, p.Values["x1"]);
            Assert.Equal(m.Y1, p.Values["y1"]);
            Assert.Equal(m.X2, p.Values["x2"]);
            Assert.Equal(m.Y2, p.Values["y2"]);
            Assert.Equal(m.Production, p.Values["productionCode"]);
            Assert.Equal(Synthetic.GroupLabels[m.Group], p.GroupLabel);

            Assert.Equal(m.Substances.Count, p.Children.Count);
            for (var j = 0; j < m.Substances.Count; j++)
            {
                var sm = m.Substances[j];
                var sc = p.Children[j];

                Assert.Equal(sm.Code, sc.Key);
                Assert.Equal(sm.Name, sc.Values["substanceName"]);
                Assert.Equal(sm.GramsPerSecond, sc.Values["gramsPerSecond"]);
                Assert.Equal(sm.MgPerNm3, sc.Values["mgPerNm3"]);
                Assert.Equal(sm.TonsPerYear, sc.Values["tonsPerYear"]);
            }
        }
    }

    [Fact]
    public void Spot_values_cover_the_traps_of_the_form()
    {
        var parsed = Parse(Synthetic.Bytes);

        // NN_suffix code, text '0003', numeric 4 -> '0004', 6001-like numbers.
        Assert.Equal("0036_pod", parsed.Parents[100].Key);
        Assert.Equal("0001", parsed.Parents[0].Key);
        Assert.Equal("0004", parsed.Parents[1].Key);

        // The smallest value survives intact; an area source has no diameter; an empty cell is null, not zero.
        Assert.Contains(parsed.Parents.SelectMany(p => p.Children), c => (decimal?)c.Values["gramsPerSecond"] == 3.5e-12m);
        Assert.Contains(parsed.Parents, p => p.Values["sourceType"] as string == "P1" && p.Values["diameter"] is null);
        Assert.Null(parsed.Parents[5].Children[0].Values["tonsPerYear"]);
        Assert.Contains(parsed.Parents.SelectMany(p => p.Children), c => c.Values["gramsPerSecond"] is null);

        // Substance codes of both cell kinds meet in one key space.
        var codes = parsed.Parents.SelectMany(p => p.Children).Select(c => c.Key).Distinct().ToList();
        Assert.Contains(codes, c => c.StartsWith('0'));
        Assert.Contains(codes, c => !c.StartsWith('0'));
        Assert.All(codes, c => Assert.Equal(4, c.Length));
    }

    [Fact]
    public void Fill_down_stops_at_the_source_columns_substance_cells_are_never_filled()
    {
        var parsed = Parse(Synthetic.Bytes);
        var multi = parsed.Parents.First(p => p.Children.Count >= 3);

        // quantity (D) is merged over every substance row: read once. mg/nm3 (Z) varies per row, including blanks.
        Assert.NotNull(multi.Values["quantity"]);
        Assert.True(multi.Children.Select(c => c.Values["mgPerNm3"]).Distinct().Count() > 1);
    }

    // ---------- the whole flow on a workbook ----------

    private static ExcelRegistryImporter Importer(FakeRegistryImportTarget target)
    {
        var nae = new FakeReferenceResolver(
            INaeResolver.NaeKind, Synthetic.Model.Select(m => m.Nae));
        var substances = new FakeReferenceResolver(
            ISubstanceResolver.SubstanceKind, Synthetic.Model.SelectMany(m => m.Substances).Select(s => s.Code));

        return new ExcelRegistryImporter(new RegistryImportService(target, [nae, substances]));
    }

    private static readonly ImportScope Scope = new("permit-1", "Atyrau/Offshore");

    [Fact]
    public async Task Preview_commit_and_a_second_preview_agree_on_the_workbook()
    {
        var target = new FakeRegistryImportTarget();
        var importer = Importer(target);
        var request = new ImportRequest(Scope, ProfileCode: BuiltInImportProfiles.EraTable33Code);

        RegistryImportPreview preview;
        using (var file = new MemoryStream(Synthetic.Bytes))
        {
            preview = await importer.PreviewAsync(file, Era, request, default);
        }

        Assert.True(preview.CanApply);
        Assert.Equal(new ImportDiffCounts(373, 0, 0, 0, 0, 2624), preview.Diff);
        Assert.Empty(target.Applied);

        ImportCommitResult committed;
        using (var file = new MemoryStream(Synthetic.Bytes))
        {
            committed = await importer.CommitAsync(file, Era, request with { ExpectedFingerprint = preview.Fingerprint }, default);
        }

        Assert.Equal(preview.Fingerprint, committed.Plan.Fingerprint);
        Assert.Equal(64, committed.Plan.FileSha256!.Length);
        Assert.Equal(373, target.Stored.Count);
        Assert.Equal(2624, target.Stored.Sum(p => p.Children.Count));

        RegistryImportPreview again;
        using (var file = new MemoryStream(Synthetic.Bytes))
        {
            again = await importer.PreviewAsync(file, Era, request, default);
        }

        Assert.Equal(new ImportDiffCounts(0, 0, 373, 0, 0, 2624), again.Diff);
        Assert.Equal(373, again.Replace.ThisOwner.Parents);
        Assert.Equal(2624, again.Replace.ThisOwner.Children);
    }

    [Fact]
    public async Task A_stream_that_is_not_a_workbook_is_refused_before_anything_else()
    {
        var importer = Importer(new FakeRegistryImportTarget());

        using var junk = new MemoryStream("not a workbook"u8.ToArray());
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => importer.PreviewAsync(junk, Era, new ImportRequest(Scope), default));

        Assert.Equal("ECR-IMP-0422", ex.ErrorCode);
        Assert.Equal("err.ECR-IMP-0422.notAWorkbook", ex.Details!["messageKey"]);
    }

    [Fact]
    public async Task A_sheet_named_in_the_profile_that_the_file_lacks_is_refused()
    {
        var importer = Importer(new FakeRegistryImportTarget());
        var spec = RegistryImportSpec.Parse(Era.ToJson().Replace("\"sheet\":null", "\"sheet\":\"Nope\"", StringComparison.Ordinal));

        using var file = new MemoryStream(Synthetic.Bytes);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => importer.PreviewAsync(file, spec, new ImportRequest(Scope), default));

        Assert.Equal("err.ECR-IMP-0422.importSheetMissing", ex.Details!["messageKey"]);
    }

    [Fact]
    public async Task A_workbook_with_one_bad_cell_is_refused_as_a_whole()
    {
        // Break one t/year cell of the generated file and import it.
        byte[] broken;
        using (var src = new MemoryStream(Synthetic.Bytes))
        using (var wb = new ClosedXML.Excel.XLWorkbook(src))
        {
            wb.Worksheets.First().Cell(Synthetic.FirstSourceRow + 3, 27).Value = "n0t a number";
            using var dst = new MemoryStream();
            wb.SaveAs(dst);
            broken = dst.ToArray();
        }

        var target = new FakeRegistryImportTarget();
        using var file = new MemoryStream(broken);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Importer(target).CommitAsync(file, Era, new ImportRequest(Scope), default));

        Assert.Equal("err.ECR-IMP-0422.regRejected", ex.Details!["messageKey"]);
        Assert.Empty(target.Applied);
        Assert.Empty(target.Stored);
    }

    // ---------- the real file, locally only ----------

    [Fact]
    public void The_real_file_when_present_has_the_counts_373_2624_55()
    {
        // The real "7) Параметры_2024_final.xlsx" holds a company's emission filing and its document properties name people:
        // it is never committed. Point ECR_NAE_SAMPLE_XLSX at it to check the profile against the real thing.
        if (Environment.GetEnvironmentVariable("ECR_NAE_SAMPLE_XLSX") is not { Length: > 0 } path || !File.Exists(path))
        {
            return;
        }

        var parsed = Parse(File.ReadAllBytes(path));

        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Issues.Where(i => i.Severity == ImportSeverity.Error).Take(5).Select(i => $"{i.MessageKey} r{i.Row} {i.Field}")));
        Assert.Equal(373, parsed.Stats.Parents);
        Assert.Equal(2624, parsed.Stats.Children);
        Assert.Equal(55, parsed.Stats.DistinctChildKeys);
        Assert.Equal(27, parsed.Stats.GroupRows);
    }
}
