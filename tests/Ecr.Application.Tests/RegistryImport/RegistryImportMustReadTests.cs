using Ecr.Application.RegistryImport;
using Xunit;
using static Ecr.Application.Tests.RegistryImport.EraGrid;

namespace Ecr.Application.Tests.RegistryImport;

/// <summary>
/// ✎ D-569 (P-1123-1, «Цех»): a field the caller stores into an added column (<see cref="ImportFieldSpec.UnreadKey"/>) that reads
/// no file column is an ERROR with that key (it blocks the apply), never the warning after which the column stayed empty.
/// </summary>
public sealed class RegistryImportMustReadTests
{
    private const string Key = "err.ECR-IMP-0422.airAddedColumnUnread";

    private static readonly RegistryImportSpec Era = BuiltInImportProfiles.EraTable33;

    /// <summary>The ERA form with «Цех» (B4:B6 merged, as in the human's test file) filled on the first source.</summary>
    private static InMemoryImportGrid File()
    {
        var era = new EraGrid().Group("Site one");
        var first = era.NextRow;
        era.Source(3d, "Source A", "Т", [Sub("0333", "H2S", 0.0024d, null, 0.07d)])
            .Source(4d, "Source B", "П1", [Sub(333d, "H2S", 1.6e-07d)]);
        era.Grid.Set(first, 2, "еуіе");
        return era.Grid;
    }

    private static ImportFieldSpec Field(string? column = null, IReadOnlyList<string>? patterns = null)
        => new ImportFieldSpec { Name = "FILE_B", Level = ImportLevel.Parent, Column = column, Patterns = patterns ?? [] }.MustRead(Key, "Цех");

    private static RegistryImportSpec With(IReadOnlyList<ImportFieldSpec> fields, IReadOnlyList<HeaderNamedField>? byHeader = null)
        => Era.WithFields([.. Era.Fields, .. fields], byHeader ?? []);

    [Fact]
    public void A_stored_field_without_a_column_or_header_text_is_an_error_not_a_warning()
    {
        var parsed = RegistryImportParser.Parse(File(), With([Field()]));

        var issue = Assert.Single(parsed.Issues, i => i.Field == "FILE_B");
        Assert.Equal(ImportSeverity.Error, issue.Severity);
        Assert.Equal(Key, issue.MessageKey);
        Assert.Equal("Цех", issue.Args!["column"]);
        Assert.Empty(parsed.Parents);
    }

    [Fact]
    public void A_stored_field_whose_column_another_field_reads_is_an_error()
    {
        var parsed = RegistryImportParser.Parse(File(), With([Field(column: "C")]));

        var issue = Assert.Single(parsed.Issues, i => i.Field == "FILE_B");
        Assert.Equal(ImportSeverity.Error, issue.Severity);
        Assert.Equal(Key, issue.MessageKey);
        Assert.Equal("C", issue.Column);
    }

    [Fact]
    public void A_stored_field_read_by_its_header_name_takes_the_merged_column_B()
    {
        var spec = With([], [new HeaderNamedField(Field(), ["Цех"])]);

        var parsed = RegistryImportParser.Parse(File(), spec);

        Assert.DoesNotContain(parsed.Issues, i => i.Severity == ImportSeverity.Error);
        Assert.Equal("еуіе", parsed.Parents[0].Values["FILE_B"]);
        Assert.Null(parsed.Parents[1].Values["FILE_B"]);
    }

    [Fact]
    public void A_stored_field_whose_header_name_is_not_in_the_file_is_an_error_an_optional_offer_is_not()
    {
        var stored = RegistryImportParser.Parse(File(), With([], [new HeaderNamedField(Field(), ["Workshop"])]));
        Assert.Equal(Key, Assert.Single(stored.Issues, i => i.Severity == ImportSeverity.Error).MessageKey);

        var offered = RegistryImportParser.Parse(
            File(), With([], [new HeaderNamedField(new ImportFieldSpec { Name = "FILE_B", Level = ImportLevel.Parent }, ["Workshop"])]));
        Assert.DoesNotContain(offered.Issues, i => i.Severity == ImportSeverity.Error);
    }

    [Fact]
    public void The_unread_marker_is_never_serialised_into_a_profile()
    {
        var json = With([Field(column: "B")]).ToJson();

        Assert.DoesNotContain("unreadKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("targetLabel", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(RegistryImportSpec.Parse(json).Fields.Single(f => f.Name == "FILE_B").UnreadKey);
    }
}
