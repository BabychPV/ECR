// tests/Ecr.Application.Tests/Registries/NaeSheetTreeBuilderTests.cs
using Ecr.Application.Registries.Nae;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// NAE tree Excel import without AF (human 02.11): the template sheet → tree plan (pure). Same technical codes and business key as
/// the AF load, so a tree from either source recognises the other's nodes.
/// </summary>
public sealed class NaeSheetTreeBuilderTests
{
    private static readonly string?[] Header = ["Region", "Area", "Site", "Equipment", "NAE code", "Name_En", "Name_Ru", "Name_Kz", "On_Offshore"];

    private static NaeTreeSheet Sheet(params string?[][] rows)
        => new("HASH", "NAE", [Header, .. rows]);

    private static string?[] Row(
        string? region, string? area = null, string? site = null, string? equip = null, string? code = null,
        string? en = null, string? ru = null, string? kz = null, string? onOff = null)
        => [region, area, site, equip, code, en, ru, kz, onOff];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]    public void Rows_build_the_same_codes_and_business_key_as_the_af_load()
    {
        var plan = NaeSheetTreeBuilder.Build(Sheet(
            Row("Atyrau", "Offshore", "Island_A", null, "85", "Gen 85", "Ген 85"),
            Row("Atyrau", "Offshore", "Island_A", "Comp 1", "1086"),
            Row("Mangistau", null, "ZPK 1", null, "1085")));

        Assert.True(plan.HeaderFound);
        Assert.Empty(plan.Issues);
        Assert.Equal(3, plan.RowsRead);

        var byCode = plan.Nodes.ToDictionary(n => n.Code);
        Assert.Equal(
            ["A_ATYRAU_OFFSHORE", "G_ATYRAU_OFFSHORE_ISLAND_A_COMP_1", "N_ATYRAU_OFFSHORE_0085", "N_ATYRAU_OFFSHORE_1086", "N_MANGISTAU_NA_1085", "R_ATYRAU", "R_MANGISTAU", "S_ATYRAU_OFFSHORE_ISLAND_A", "S_MANGISTAU_NA_ZPK_1"],
            byCode.Keys.Order(StringComparer.Ordinal).ToArray());

        var src = byCode["N_ATYRAU_OFFSHORE_0085"];
        Assert.Equal("0085", src.Fields[NaeTreeSchema.NaeCodeField]);
        Assert.Equal("ATYRAU", src.Fields[NaeTreeSchema.RegionField]);
        Assert.Equal("OFFSHORE", src.Fields[NaeTreeSchema.AreaField]);
        Assert.Equal("Gen 85", src.Display["en"]);
        Assert.Equal("Ген 85", src.Display["ru"]);
        Assert.Equal("S|ATYRAU|OFFSHORE|ISLAND_A", src.ParentKey);

        // A region without areas: the no-area marker in the key, the site straight under the region.
        Assert.Equal("-", byCode["N_MANGISTAU_NA_1085"].Fields[NaeTreeSchema.AreaField]);
        Assert.Equal("R|MANGISTAU", byCode["S_MANGISTAU_NA_ZPK_1"].ParentKey);

        // Parents before children.
        var index = plan.Nodes.Select((n, i) => (n.Key, i)).ToDictionary(x => x.Key, x => x.i);
        Assert.All(plan.Nodes.Where(n => n.ParentKey is not null), n => Assert.True(index[n.ParentKey!] < index[n.Key]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]    public void A_group_only_row_creates_groups_and_names_the_deepest_one()
    {
        var plan = NaeSheetTreeBuilder.Build(Sheet(
            Row("Atyrau", null, null, null, null, "Atyrau region", "Атырауская область", "Атырау облысы"),
            Row("Atyrau", "Onshore")));

        Assert.Empty(plan.Issues);
        var region = plan.Nodes.Single(n => n.Code == "R_ATYRAU");
        Assert.Equal("Atyrau region", region.Display["en"]);
        Assert.Equal("Атырау облысы", region.Display["kz"]);
        Assert.Equal("Onshore", plan.Nodes.Single(n => n.Code == "A_ATYRAU_ONSHORE").Display["en"]);
        Assert.DoesNotContain(plan.Nodes, n => n.LevelCode == NaeTreeSchema.LevelSource);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]    [InlineData(null, "Offshore", "OFFSHORE")]
    [InlineData("Offshore", null, "OFFSHORE")]
    [InlineData("offshore", "OFFSHORE", "OFFSHORE")]
    public void Area_wins_and_on_offshore_is_the_fallback(string? area, string? onOff, string expected)
    {
        var plan = NaeSheetTreeBuilder.Build(Sheet(Row("Atyrau", area, "Island_A", null, "1085", onOff: onOff)));

        Assert.Empty(plan.Issues);
        Assert.Equal(expected, plan.Nodes.Single(n => n.LevelCode == NaeTreeSchema.LevelSource).Fields[NaeTreeSchema.AreaField]);
    }

    public static TheoryData<string?[], string> BadRows => new()
    {
        { Row(null, "Offshore", "Island_A", null, "1085"), "regionRequired" },
        { Row("Atyrau", "Offshore", null, null, "1085"), "siteRequired" },
        { Row("Atyrau", "Offshore", null, "Comp 1"), "siteRequired" },
        { Row("Atyrau", "Offshore", "Island_A", null, "1.5"), "badCode" },
        { Row("Atyrau", "Offshore", "Island_A", null, "1085", onOff: "Onshore"), "areaMismatch" },
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]    [MemberData(nameof(BadRows))]
    public void A_bad_row_is_an_error_on_its_row_and_creates_nothing(string?[] row, string code)
    {
        var plan = NaeSheetTreeBuilder.Build(Sheet(Row("Mangistau"), row));

        var issue = Assert.Single(plan.Issues);
        Assert.Equal("error", issue.Severity);
        Assert.Equal(code, issue.Code);
        Assert.Equal(3, issue.Row);
        Assert.Equal(["R_MANGISTAU"], plan.Nodes.Select(n => n.Code).ToArray());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]    public void The_same_business_key_twice_is_an_error_but_the_same_code_in_another_area_is_not()
    {
        var plan = NaeSheetTreeBuilder.Build(Sheet(
            Row("Atyrau", "Offshore", "Island_A", null, "1085"),
            Row("Atyrau", "Onshore", "GTP", null, "1085"),
            Row("Atyrau", "Offshore", "Island_B", null, "1085")));

        var issue = Assert.Single(plan.Issues);
        Assert.Equal(("duplicateInFile", 4), (issue.Code, issue.Row));
        Assert.Equal(2, plan.Nodes.Count(n => n.LevelCode == NaeTreeSchema.LevelSource));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]    public void The_header_is_found_below_a_title_and_empty_rows_are_skipped()
    {
        var plan = NaeSheetTreeBuilder.Build(new NaeTreeSheet(
            "HASH",
            "NAE",
            [["NAE structure"], [], ["region", "nae_code", "SITE"], [null, null, null], ["Atyrau", "1085", "Island"]]));

        Assert.True(plan.HeaderFound);
        Assert.Equal(1, plan.RowsRead);
        Assert.Equal(5, plan.RowOf["N|1085|ATYRAU|-"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]    public void No_header_is_one_file_error()
    {
        var plan = NaeSheetTreeBuilder.Build(new NaeTreeSheet("HASH", "S", [["a", "b"], ["1", "2"]]));

        Assert.False(plan.HeaderFound);
        Assert.Equal("headerNotFound", Assert.Single(plan.Issues).Code);
        Assert.Empty(plan.Nodes);
    }
}
