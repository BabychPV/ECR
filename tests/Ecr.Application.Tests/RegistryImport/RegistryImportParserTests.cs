using Ecr.Application.Errors;
using Ecr.Application.RegistryImport;
using Xunit;
using static Ecr.Application.Tests.RegistryImport.EraGrid;

namespace Ecr.Application.Tests.RegistryImport;

public sealed class RegistryImportParserTests
{
    private static readonly RegistryImportSpec Era = BuiltInImportProfiles.EraTable33;

    private static ParsedImport Parse(EraGrid era, RegistryImportSpec? spec = null)
        => RegistryImportParser.Parse(era.Grid, spec ?? Era);

    private static EraGrid TwoSources()
        => new EraGrid()
            .Group("Group one")
            .Source(3d, "Source A", "Т", [Sub("0333", "H2S", 0.0024d, null, 0.07d), Sub(2735d, "Oil", 0.0002d, 1.263d, 0.0063d), Sub(301d, "NO2", 1e-12d, 5d, 3.5e-12d)], diameter: 0.25d)
            .Source("0004", "Source B", "П1", [Sub(333d, "H2S", 1.6e-07d)])
            .Group("Group two")
            .Source(6001d, "Source C  with   spaces", "T", [Sub("2902", "Dust", 0.0007d, null, 0.0069155d), Sub("2908", "Dust 2", 0.0004d, null, 0.0046103d)], height: 7d)
            .Totals();

    // ---------- header ----------

    [Fact]
    public void Header_block_and_data_start_are_found_from_the_column_number_row()
    {
        var parsed = Parse(TwoSources());

        Assert.NotNull(parsed.Header);
        Assert.Equal(4, parsed.Header.HeaderFromRow);
        Assert.Equal(6, parsed.Header.HeaderToRow);
        Assert.Equal(8, parsed.Header.DataFromRow);
        Assert.False(parsed.HasErrors, string.Join("; ", parsed.Issues.Select(i => i.MessageKey + " " + i.Field)));
    }

    [Fact]
    public void Columns_are_matched_through_merged_multi_row_headers_and_unit_qualifiers()
    {
        var map = Parse(TwoSources()).Header!.Mapped.ToDictionary(m => m.Field, m => m.ColumnLetter);

        Assert.Equal("A", map["productionCode"]);
        Assert.Equal("C", map["sourceName"]);
        Assert.Equal("D", map["quantity"]);
        Assert.Equal("E", map["hoursPerYear"]);
        Assert.Equal("F", map["emissionPointName"]);
        Assert.Equal("G", map["naeCode"]);
        Assert.Equal("H", map["sourceType"]);
        Assert.Equal("I", map["height"]);
        Assert.Equal("J", map["diameter"]);
        Assert.Equal("K", map["velocity"]);
        Assert.Equal("L", map["volume"]);
        Assert.Equal("M", map["temperature"]);
        Assert.Equal("N", map["x1"]);
        Assert.Equal("O", map["y1"]);
        Assert.Equal("P", map["x2"]);
        Assert.Equal("Q", map["y2"]);
        Assert.Equal("W", map["substanceCode"]);
        Assert.Equal("X", map["substanceName"]);
        Assert.Equal("Y", map["gramsPerSecond"]);
        Assert.Equal("Z", map["mgPerNm3"]);
        Assert.Equal("AA", map["tonsPerYear"]);
    }

    [Fact]
    public void Header_parts_of_a_merged_range_are_read_once()
    {
        // Y4:AA5 spans three columns and two rows: its text must appear once per column, not once per cell.
        var mapped = Parse(TwoSources()).Header!.Mapped.First(m => m.Field == "gramsPerSecond");

        Assert.Equal(1, CountOccurrences(mapped.HeaderText, "Выбросы"));
    }

    private static int CountOccurrences(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }

    [Fact]
    public void Columns_without_a_field_are_reported_with_whether_they_hold_data()
    {
        var unmapped = Parse(TwoSources()).Header!.Unmapped.ToDictionary(u => u.ColumnLetter);

        Assert.False(unmapped["B"].HasData);
        Assert.False(unmapped["R"].HasData);
        Assert.False(unmapped["V"].HasData);
        Assert.True(unmapped["AB"].HasData);
    }

    [Fact]
    public void A_sheet_without_a_header_block_is_refused_with_a_message_key()
    {
        var grid = new InMemoryImportGrid().Set(1, 1, "just").Set(2, 1, "text");

        var ex = Assert.Throws<BusinessRuleException>(() => RegistryImportParser.Parse(grid, Era));

        Assert.Equal("ECR-IMP-0422", ex.ErrorCode);
        Assert.Equal(RegistryImportKeys.HeaderNotFound, ex.Details!["messageKey"]);
    }

    [Fact]
    public void Explicit_header_rows_work_without_a_number_row()
    {
        var grid = new InMemoryImportGrid()
            .Set(1, 1, "Code").Set(1, 2, "Subst").Set(1, 3, "Val")
            .Set(2, 1, "A1").Set(2, 2, "S1").Set(2, 3, 5d);
        var spec = new RegistryImportSpec
        {
            Header = new HeaderSpec { FromRow = 1, ToRow = 1 },
            ParentKeyField = "p",
            ChildKeyField = "c",
            Fields =
            [
                new ImportFieldSpec { Name = "p", Level = ImportLevel.Parent, Patterns = ["Code"], Required = true },
                new ImportFieldSpec { Name = "c", Level = ImportLevel.Child, Patterns = ["Subst"], Required = true },
                new ImportFieldSpec { Name = "v", Level = ImportLevel.Child, Patterns = ["Val"], Parser = ImportParser.Decimal },
            ],
        }.Validated();

        var parsed = RegistryImportParser.Parse(grid, spec);

        Assert.Equal(2, parsed.Header!.DataFromRow);
        Assert.Equal(5m, parsed.Parents.Single().Children.Single().Values["v"]);
    }

    [Fact]
    public void A_required_column_that_is_missing_stops_the_parse_with_an_error()
    {
        var era = TwoSources();
        var spec = new RegistryImportSpec
        {
            ParentKeyField = Era.ParentKeyField,
            ChildKeyField = Era.ChildKeyField,
            Fields = [.. Era.Fields.Where(f => f.Name != "tonsPerYear"), new ImportFieldSpec
            {
                Name = "tonsPerYear",
                Level = ImportLevel.Child,
                Patterns = ["No such column"],
                Required = true,
            }],
        }.Validated();

        var parsed = Parse(era, spec);

        Assert.True(parsed.HasErrors);
        Assert.Contains(parsed.Issues, i => i.MessageKey == RegistryImportKeys.ColumnMissing && i.Field == "tonsPerYear");
        Assert.Empty(parsed.Parents);
    }

    [Fact]
    public void A_missing_optional_column_is_only_a_warning()
    {
        var spec = new RegistryImportSpec
        {
            GroupRows = Era.GroupRows,
            ParentKeyField = Era.ParentKeyField,
            ChildKeyField = Era.ChildKeyField,
            Fields = [.. Era.Fields.Where(f => f.Name != "diameter"), new ImportFieldSpec
            {
                Name = "diameter",
                Level = ImportLevel.Parent,
                Patterns = ["No such column"],
            }],
        }.Validated();

        var parsed = Parse(TwoSources(), spec);

        Assert.False(parsed.HasErrors);
        Assert.Contains(parsed.Issues, i => i.Severity == ImportSeverity.Warning && i.MessageKey == RegistryImportKeys.ColumnMissing);
        Assert.Equal(3, parsed.Parents.Count);
    }

    [Fact]
    public void Two_columns_matching_one_field_is_an_ambiguity_error()
    {
        var spec = new RegistryImportSpec
        {
            ParentKeyField = Era.ParentKeyField,
            ChildKeyField = Era.ChildKeyField,
            Fields = [.. Era.Fields.Where(f => f.Name != "gramsPerSecond"), new ImportFieldSpec
            {
                Name = "gramsPerSecond",
                Level = ImportLevel.Child,
                Patterns = ["Выбросы загрязняющего"], // no unit qualifier: Y, Z, AA
                Parser = ImportParser.Decimal,
            }],
        }.Validated();

        var parsed = Parse(TwoSources(), spec);

        var issue = Assert.Single(parsed.Issues, i => i.MessageKey == RegistryImportKeys.ColumnAmbiguous);
        Assert.Equal("Y, Z, AA", issue.Args!["columns"]);
    }

    [Fact]
    public void One_column_claimed_by_two_fields_is_an_error()
    {
        var spec = new RegistryImportSpec
        {
            ParentKeyField = Era.ParentKeyField,
            ChildKeyField = Era.ChildKeyField,
            Fields = [.. Era.Fields, new ImportFieldSpec { Name = "twin", Level = ImportLevel.Parent, Column = "G" }],
        }.Validated();

        Assert.Contains(Parse(TwoSources(), spec).Issues, i => i.MessageKey == RegistryImportKeys.ColumnClaimed && i.Field == "twin");
    }

    [Fact]
    public void An_explicit_column_bypasses_header_matching()
    {
        var spec = new RegistryImportSpec
        {
            GroupRows = Era.GroupRows,
            ParentKeyField = Era.ParentKeyField,
            ChildKeyField = Era.ChildKeyField,
            Fields = [.. Era.Fields, new ImportFieldSpec { Name = "workshop", Level = ImportLevel.Parent, Column = "B" }],
        }.Validated();

        var parsed = Parse(TwoSources(), spec);

        Assert.Contains(parsed.Header!.Mapped, m => m.Field == "workshop" && m.ColumnLetter == "B");
    }

    // ---------- rows ----------

    [Fact]
    public void Sources_and_substances_are_read_as_two_levels()
    {
        var parsed = Parse(TwoSources());

        Assert.Equal(3, parsed.Stats.Parents);
        Assert.Equal(6, parsed.Stats.Children);
        Assert.Equal(["0003", "0004", "6001"], parsed.Parents.Select(p => p.Key));
        Assert.Equal(3, parsed.Parents[0].Children.Count);
        Assert.Single(parsed.Parents[1].Children);
        Assert.Equal(2, parsed.Parents[2].Children.Count);
    }

    [Fact]
    public void Codes_of_both_levels_are_normalised_whatever_the_cell_type()
    {
        var parsed = Parse(TwoSources());

        Assert.Equal(["0333", "2735", "0301"], parsed.Parents[0].Children.Select(c => c.Key));
        Assert.Equal("0333", parsed.Parents[1].Children.Single().Key);
    }

    [Fact]
    public void Source_values_come_from_the_first_row_and_are_parsed()
    {
        var first = Parse(TwoSources()).Parents[0].Values;

        Assert.Equal("Source A", first["sourceName"]);
        Assert.Equal(131m, first["quantity"]);          // text-number
        Assert.Equal(8784m, first["hoursPerYear"]);
        Assert.Equal("T", first["sourceType"]);         // Cyrillic Т
        Assert.Equal(9.5m, first["height"]);
        Assert.Equal(0.25m, first["diameter"]);
        Assert.Equal(34.1m, first["temperature"]);
        Assert.Equal(9597489m, first["x1"]);
        Assert.Equal("101", first["productionCode"]);
    }

    [Fact]
    public void An_area_source_has_no_diameter_and_that_is_not_an_error()
    {
        var parsed = Parse(TwoSources());

        Assert.Equal("P1", parsed.Parents[1].Values["sourceType"]);
        Assert.Null(parsed.Parents[1].Values["diameter"]);
        Assert.False(parsed.HasErrors);
    }

    [Fact]
    public void A_latin_type_letter_is_folded_to_the_same_choice()
        => Assert.Equal("T", Parse(TwoSources()).Parents[2].Values["sourceType"]);

    [Fact]
    public void Text_is_cleaned_of_double_spaces()
        => Assert.Equal("Source C with spaces", Parse(TwoSources()).Parents[2].Values["sourceName"]);

    [Fact]
    public void Substance_values_keep_every_digit_of_tiny_numbers()
    {
        var third = Parse(TwoSources()).Parents[0].Children[2].Values;

        Assert.Equal(1e-12m, third["gramsPerSecond"]);
        Assert.Equal(3.5e-12m, third["tonsPerYear"]);
        Assert.Equal(5m, third["mgPerNm3"]);
    }

    [Fact]
    public void Empty_substance_numbers_are_null_not_zero()
    {
        var first = Parse(TwoSources()).Parents[0].Children[0].Values;

        Assert.Null(first["mgPerNm3"]);
        Assert.Equal(0.0024m, first["gramsPerSecond"]);
    }

    [Fact]
    public void Group_rows_become_context_not_data()
    {
        var parsed = Parse(TwoSources());

        Assert.Equal(["Group one", "Group two"], parsed.Groups.Select(g => g.Label));
        Assert.Equal("Group one", parsed.Parents[0].GroupLabel);
        Assert.Equal("Group one", parsed.Parents[1].GroupLabel);
        Assert.Equal("Group two", parsed.Parents[2].GroupLabel);
        Assert.Equal(parsed.Groups[1].Row, parsed.Parents[2].GroupRow);
    }

    [Fact]
    public void A_label_in_the_group_column_is_a_group_even_when_not_merged_if_nothing_else_is_in_the_row()
    {
        var grid = new EraGrid();
        grid.Grid.Set(8, 1, "Unmerged label");
        grid.Source(3d, "S", "T", [Sub(333d, "x")]);

        var parsed = Parse(grid);

        Assert.Equal("Unmerged label", Assert.Single(parsed.Groups).Label);
        Assert.Equal("Unmerged label", parsed.Parents.Single().GroupLabel);
    }

    [Fact]
    public void Totals_blank_rows_and_formula_rows_are_skipped_and_counted()
    {
        var parsed = Parse(TwoSources());

        Assert.Equal(3, parsed.Stats.SkippedRows);   // TOTAL + two block rows (blank rows are not counted)
        Assert.Equal(3, parsed.Parents.Count);
    }

    [Fact]
    public void A_formula_in_a_key_cell_is_a_totals_row()
    {
        var era = new EraGrid().Source(3d, "S", "T", [Sub(333d, "x")]);
        era.Grid.Set(era.NextRow, SubstanceCodeColumn, 999d, hasFormula: true).Set(era.NextRow, 27, 5d);

        var parsed = Parse(era);

        Assert.Single(parsed.Parents);
        Assert.Equal(1, parsed.Stats.SkippedRows);
    }

    [Fact]
    public void Fill_down_inside_a_vertical_merge_does_not_leak_into_substance_columns()
    {
        // The source is merged over three rows; the quantity (D) is read from the merge anchor, while the substance columns
        // W-AB are read per row only: a blank mg/nm3 of the second substance stays blank.
        var parsed = Parse(TwoSources());
        var source = parsed.Parents[0];

        Assert.Equal(3, source.Children.Count);
        Assert.Equal(131m, source.Values["quantity"]);
        Assert.Null(source.Children[0].Values["mgPerNm3"]);
        Assert.Equal(1.263m, source.Children[1].Values["mgPerNm3"]);
    }

    private static RegistryImportSpec TwoColumnSpec(FillDown fill) => new RegistryImportSpec
    {
        Header = new HeaderSpec { FromRow = 1, ToRow = 1 },
        ParentKeyField = "p",
        ChildKeyField = "c",
        Fields =
        [
            new ImportFieldSpec { Name = "p", Level = ImportLevel.Parent, Patterns = ["Code"], Required = true },
            new ImportFieldSpec { Name = "qty", Level = ImportLevel.Parent, Patterns = ["Qty"], Parser = ImportParser.Decimal, FillDown = fill },
            new ImportFieldSpec { Name = "c", Level = ImportLevel.Child, Patterns = ["Subst"], Required = true },
        ],
    }.Validated();

    private static InMemoryImportGrid QtyMergedAboveTheParent()
        => new InMemoryImportGrid()
            .Set(1, 1, "Code").Set(1, 2, "Qty").Set(1, 3, "Subst")
            .Set(2, 2, 5d).Merge(2, 2, 3, 2)          // Qty is merged over rows 2-3, the anchor is row 2
            .Set(2, 1, "A1").Set(2, 3, "S1")
            .Set(3, 1, "A2").Set(3, 3, "S1");          // a second parent starts on the merge's second row

    [Fact]
    public void Merge_fill_reads_the_anchor_when_the_parent_starts_inside_a_merge()
    {
        var parsed = RegistryImportParser.Parse(QtyMergedAboveTheParent(), TwoColumnSpec(FillDown.Merge));

        Assert.Equal([5m, 5m], parsed.Parents.Select(p => p.Values["qty"]));
    }

    [Fact]
    public void Without_merge_fill_the_covered_cell_is_blank()
    {
        var parsed = RegistryImportParser.Parse(QtyMergedAboveTheParent(), TwoColumnSpec(FillDown.None));

        Assert.Equal([5m, null], parsed.Parents.Select(p => p.Values["qty"]));
    }

    [Fact]
    public void Blank_fill_down_carries_the_last_value_of_the_column()
    {
        var grid = new InMemoryImportGrid()
            .Set(1, 1, "Site").Set(1, 2, "Code").Set(1, 3, "Subst")
            .Set(2, 1, "North").Set(2, 2, "A1").Set(2, 3, "S1")
            .Set(3, 2, "A2").Set(3, 3, "S1")
            .Set(4, 1, "South").Set(4, 2, "A3").Set(4, 3, "S1");
        var spec = new RegistryImportSpec
        {
            Header = new HeaderSpec { FromRow = 1, ToRow = 1 },
            ParentKeyField = "p",
            ChildKeyField = "c",
            Fields =
            [
                new ImportFieldSpec { Name = "site", Level = ImportLevel.Parent, Patterns = ["Site"], FillDown = FillDown.Blank },
                new ImportFieldSpec { Name = "p", Level = ImportLevel.Parent, Patterns = ["Code"], Required = true },
                new ImportFieldSpec { Name = "c", Level = ImportLevel.Child, Patterns = ["Subst"], Required = true },
            ],
        }.Validated();

        var parsed = RegistryImportParser.Parse(grid, spec);

        Assert.Equal(["North", "North", "South"], parsed.Parents.Select(p => p.Values["site"]));
    }

    [Fact]
    public void Without_fill_down_a_blank_stays_blank()
    {
        var grid = new InMemoryImportGrid()
            .Set(1, 1, "Site").Set(1, 2, "Code").Set(1, 3, "Subst")
            .Set(2, 1, "North").Set(2, 2, "A1").Set(2, 3, "S1")
            .Set(3, 2, "A2").Set(3, 3, "S1");
        var spec = new RegistryImportSpec
        {
            Header = new HeaderSpec { FromRow = 1, ToRow = 1 },
            ParentKeyField = "p",
            ChildKeyField = "c",
            Fields =
            [
                new ImportFieldSpec { Name = "site", Level = ImportLevel.Parent, Patterns = ["Site"], FillDown = FillDown.None },
                new ImportFieldSpec { Name = "p", Level = ImportLevel.Parent, Patterns = ["Code"], Required = true },
                new ImportFieldSpec { Name = "c", Level = ImportLevel.Child, Patterns = ["Subst"], Required = true },
            ],
        }.Validated();

        var parsed = RegistryImportParser.Parse(grid, spec);

        Assert.Equal(["North", null], parsed.Parents.Select(p => p.Values["site"]));
    }

    // ---------- errors ----------

    [Fact]
    public void The_same_source_twice_is_a_duplicate_error_naming_both_rows()
    {
        var era = new EraGrid()
            .Source(3d, "S1", "T", [Sub(333d, "x")])
            .Source("0003", "S1 again", "T", [Sub(333d, "x")]);

        var parsed = Parse(era);

        var issue = Assert.Single(parsed.Issues, i => i.MessageKey == RegistryImportKeys.DuplicateParent);
        Assert.Equal("0003", issue.Args!["key"]);
        Assert.Equal("9", issue.Args["firstRow"]);
        Assert.Single(parsed.Parents);
    }

    [Fact]
    public void The_same_substance_twice_under_one_source_is_a_duplicate_error()
    {
        var era = new EraGrid().Source(3d, "S1", "T", [Sub(333d, "x"), Sub("0333", "x again")]);

        var parsed = Parse(era);

        Assert.Contains(parsed.Issues, i => i.MessageKey == RegistryImportKeys.DuplicateChild && i.Args!["key"] == "0333");
        Assert.Single(parsed.Parents.Single().Children);
    }

    [Fact]
    public void The_same_substance_under_different_sources_is_fine()
    {
        var era = new EraGrid()
            .Source(3d, "S1", "T", [Sub(333d, "x")])
            .Source(4d, "S2", "T", [Sub(333d, "x")]);

        Assert.False(Parse(era).HasErrors);
    }

    [Fact]
    public void A_substance_row_before_any_source_is_an_error()
    {
        var era = new EraGrid();
        era.Grid.Set(9, SubstanceCodeColumn, 333d);

        Assert.Contains(Parse(era).Issues, i => i.MessageKey == RegistryImportKeys.ChildWithoutParent && i.Row == 9);
    }

    [Fact]
    public void An_unreadable_number_names_the_row_the_column_and_the_value()
    {
        var era = new EraGrid().Source(3d, "S1", "T", [Sub(333d, "x", gs: "oops")]);

        var issue = Assert.Single(Parse(era).Issues, i => i.MessageKey == RegistryImportKeys.ValueInvalid);

        Assert.Equal(9, issue.Row);
        Assert.Equal("Y", issue.Column);
        Assert.Equal("gramsPerSecond", issue.Field);
        Assert.Equal("oops", issue.Args!["value"]);
    }

    [Fact]
    public void A_required_value_that_is_empty_is_an_error()
    {
        var era = new EraGrid().Source(3d, "S1", "T", [Sub(333d, "x")], height: "-");

        Assert.Contains(Parse(era).Issues, i => i.MessageKey == RegistryImportKeys.ValueRequired && i.Field == "height");
    }

    [Fact]
    public void An_unreadable_key_does_not_cascade_into_child_without_parent_errors()
    {
        var era = new EraGrid().Source(1.5d, "S1", "T", [Sub(333d, "x"), Sub(334d, "y")]);

        var parsed = Parse(era);

        Assert.Single(parsed.Issues, i => i.Severity == ImportSeverity.Error);
        Assert.Empty(parsed.Parents);
    }

    [Fact]
    public void A_source_without_substances_is_a_warning()
    {
        var era = new EraGrid().Source(3d, "S1", "T", []);

        var parsed = Parse(era);

        Assert.False(parsed.HasErrors);
        Assert.Contains(parsed.Issues, i => i.MessageKey == RegistryImportKeys.ParentWithoutChildren);
        Assert.Single(parsed.Parents);
    }

    [Fact]
    public void An_empty_sheet_body_is_an_error()
        => Assert.Contains(Parse(new EraGrid()).Issues, i => i.MessageKey == RegistryImportKeys.NoRows);

    [Fact]
    public void An_empty_tons_per_year_cell_is_a_per_row_warning_not_an_error()
    {
        var era = new EraGrid().Source(3d, "S1", "T", [Sub(333d, "x", 0.1d, null, null), Sub(334d, "y", 0.1d, null, 0.5d)]);

        var parsed = Parse(era);

        Assert.False(parsed.HasErrors);
        var warning = Assert.Single(parsed.Issues);
        Assert.Equal((ImportSeverity.Warning, RegistryImportKeys.ValueEmpty, "tonsPerYear"), (warning.Severity, warning.MessageKey, warning.Field));
        Assert.NotNull(warning.Row);
        Assert.False(string.IsNullOrEmpty(warning.Column));
        Assert.Equal(2, parsed.Stats.Children);
    }

    [Fact]
    public void Substance_values_without_a_substance_code_are_an_error_with_row_and_column()
    {
        var era = new EraGrid().Source(3d, "S1", "T", [Sub(333d, "x", 0.1d, null, 0.2d), Sub("", "no code", 0.3d, null, 0.4d)]);

        var parsed = Parse(era);

        Assert.True(parsed.HasErrors);
        var error = Assert.Single(parsed.Issues, i => i.Severity == ImportSeverity.Error);
        Assert.Equal((RegistryImportKeys.ValueRequired, "substanceCode"), (error.MessageKey, error.Field));
        Assert.NotNull(error.Row);
        Assert.False(string.IsNullOrEmpty(error.Column));
    }
    [Fact]
    public void Negative_height_is_refused()
    {
        var era = new EraGrid().Source(3d, "S1", "T", [Sub(333d, "x")], height: -2d);

        Assert.Contains(Parse(era).Issues, i => i.MessageKey == RegistryImportKeys.ValueNegative);
    }

    [Fact]
    public void Issues_are_capped_per_severity_but_still_make_the_parse_fail()
    {
        var era = new EraGrid();
        for (var i = 0; i < RegistryImportParser.MaxIssues + 50; i++)
        {
            era.Source(i + 1, "S", "T", [Sub(333d, "x", gs: "bad", tons: 1d)]);
        }

        var parsed = Parse(era);

        Assert.True(parsed.HasErrors);
        Assert.Equal(RegistryImportParser.MaxIssues, parsed.Issues.Count(x => x.Severity == ImportSeverity.Error));
        Assert.Equal(50, parsed.DroppedIssues);
    }

    // ---------- spec ----------

    [Fact]
    public void Spec_round_trips_through_json()
    {
        var again = RegistryImportSpec.Parse(Era.ToJson());

        Assert.Equal(Era.Fields.Count, again.Fields.Count);
        Assert.Equal(Era.ParentKeyField, again.ParentKeyField);
        Assert.Equal(Era.Fields.Single(f => f.Name == "sourceType").Choices, again.Fields.Single(f => f.Name == "sourceType").Choices);
        Assert.Equal(Era.ToJson(), again.ToJson());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"parentKeyField":"p","childKeyField":"c","fields":[{"name":"p","level":"child","patterns":["x"],"required":true}]}""")]
    [InlineData("""{"parentKeyField":"p","childKeyField":"c","fields":[{"name":"p","level":"parent","required":true},{"name":"c","level":"child","patterns":["y"],"required":true}]}""")]
    [InlineData("""{"parentKeyField":"p","childKeyField":"c","fields":[{"name":"p","level":"parent","patterns":["x"]},{"name":"c","level":"child","patterns":["y"],"required":true}]}""")]
    [InlineData("""{"parentKeyField":"p","childKeyField":"c","fields":[{"name":"p","level":"parent","patterns":["x"],"required":true},{"name":"p","level":"parent","patterns":["x"]},{"name":"c","level":"child","patterns":["y"],"required":true}]}""")]
    [InlineData("""{"parentKeyField":"p","childKeyField":"c","fields":[{"name":"p","level":"parent","patterns":["x"],"required":true},{"name":"c","level":"child","patterns":["y"],"required":true,"parser":"choice"}]}""")]
    public void An_invalid_spec_is_refused_with_a_message_key(string json)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => RegistryImportSpec.Parse(json));

        Assert.Equal(RegistryImportKeys.SpecInvalid, ex.Details!["messageKey"]);
    }

    [Theory]
    [InlineData("A", 1)]
    [InlineData("Z", 26)]
    [InlineData("AA", 27)]
    [InlineData("AB", 28)]
    [InlineData("ab", 28)]
    public void Column_letters_convert_both_ways(string letters, int index)
    {
        Assert.Equal(index, ColumnLetters.ToIndex(letters));
        Assert.Equal(letters.ToUpperInvariant(), ColumnLetters.ToLetters(index));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A1")]
    [InlineData("1")]
    [InlineData("XFE")]
    public void Not_a_column(string letters)
        => Assert.Null(ColumnLetters.ToIndex(letters));
}
