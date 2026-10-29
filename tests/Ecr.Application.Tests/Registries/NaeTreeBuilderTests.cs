// tests/Ecr.Application.Tests/Registries/NaeTreeBuilderTests.cs
using Ecr.Application.Registries.Hierarchy;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// NAE plan N2: the code normaliser and the AF element tree builder (pure functions, table-driven).
/// Mutation proofs (local): drop the zero padding → the padding rows fail; drop the leaf rule → groups turn into sources;
/// drop the business-key set → the duplicate row fails; swap region and area in the key → the cross-region row fails.
/// </summary>
public sealed class NaeTreeBuilderTests
{
    private const string Root = @"\\AF\ECR\ECR_01_Air Equipment";

    public static TheoryData<object?, string?> Codes => new()
    {
        { "1085", "1085" },
        { 85d, "0085" },
        { "85", "0085" },
        { " 85 ", "0085" },
        { "\u00A085\u00A0", "0085" },
        { "85.0", "0085" },
        { "36_pod", "0036_pod" },
        { "0036_pod", "0036_pod" },
        { 12345d, "12345" },
        { "A12", "A12" },
        { "", null },
        { "   ", null },
        { null, null },
        { -5d, null },
        { 1.5d, null },
        { "_x", "_x" },
        { 36m, "0036" },
    };

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [MemberData(nameof(Codes))]
    public void Normalize__pads_numbers_and_numeric_prefixes(object? raw, string? expected)
        => Assert.Equal(expected, NaeCodeNormalizer.Normalize(raw));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Normalize__pad_is_a_parameter()
        => Assert.Equal("00085", NaeCodeNormalizer.Normalize("85", 5));

    private static NaeAfElement E(string relative, string? region = null, string? area = null, string? en = null, string? ru = null)
        => new($@"{Root}\{relative}", relative.Split('\\')[^1], en, ru, null, region, area);

    private static NaeTreePlan Build(params NaeAfElement[] elements)
        => NaeAfTreeBuilder.Build(new NaeAfSnapshot(elements, true), Root);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Build__full_path_makes_region_area_site_equipment_source()
    {
        var plan = Build(
            E(@"Atyrau_Air\Offshore\Island_A", null, null),
            E(@"Atyrau_Air\Offshore\Island_A\Generator", "Atyrau", "Offshore"),
            E(@"Atyrau_Air\Offshore\Island_A\Generator\1085", "Atyrau", "Offshore", "Gen 1", "Ген 1"));

        Assert.Equal(
            [NaeTreeSchema.LevelRegion, NaeTreeSchema.LevelArea, NaeTreeSchema.LevelSite, NaeTreeSchema.LevelEquipment, NaeTreeSchema.LevelSource],
            plan.Nodes.Select(n => n.LevelCode).ToArray());
        var src = plan.Nodes.Single(n => n.LevelCode == NaeTreeSchema.LevelSource);
        Assert.Equal("1085", src.Fields[NaeTreeSchema.NaeCodeField]);
        Assert.Equal("ATYRAU", src.Fields[NaeTreeSchema.RegionField]);
        Assert.Equal("OFFSHORE", src.Fields[NaeTreeSchema.AreaField]);
        Assert.Equal("Gen 1", src.Display["en"]);
        Assert.Equal("Ген 1", src.Display["ru"]);
        Assert.Empty(plan.Issues);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Build__region_without_area_uses_the_dash_and_skips_the_area_level()
    {
        var plan = Build(E(@"Mangistau_Air\ZPK_1\36_pod", "Mangistau", null));

        Assert.DoesNotContain(plan.Nodes, n => n.LevelCode == NaeTreeSchema.LevelArea);
        var src = plan.Nodes.Single(n => n.LevelCode == NaeTreeSchema.LevelSource);
        Assert.Equal(NaeAfTreeBuilder.NoArea, src.Fields[NaeTreeSchema.AreaField]);
        Assert.Equal("0036_pod", src.Fields[NaeTreeSchema.NaeCodeField]);
        Assert.Equal(plan.Nodes.Single(n => n.LevelCode == NaeTreeSchema.LevelSite).Key, src.ParentKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Build__same_nae_code_in_two_regions_is_two_sources()
    {
        var plan = Build(
            E(@"Atyrau_Air\Onshore\S1\1085", "Atyrau", "Onshore"),
            E(@"Mangistau_Air\S2\1085", "Mangistau", null));

        Assert.Equal(2, plan.Nodes.Count(n => n.LevelCode == NaeTreeSchema.LevelSource));
        Assert.Equal(2, plan.Nodes.Where(n => n.LevelCode == NaeTreeSchema.LevelSource).Select(n => n.Code).Distinct().Count());
        Assert.Empty(plan.Issues);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Build__same_business_key_twice_skips_the_second()
    {
        var plan = Build(
            E(@"Atyrau_Air\Onshore\S1\1085", "Atyrau", "Onshore"),
            E(@"Atyrau_Air\Onshore\S2\1085", "Atyrau", "ONSHORE"));

        Assert.Single(plan.Nodes, n => n.LevelCode == NaeTreeSchema.LevelSource);
        var issue = Assert.Single(plan.Issues);
        Assert.Equal(("skipped", "duplicateBusinessKey"), (issue.Severity, issue.Code));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(@"Atyrau_Air\Onshore\S1\1085", null, "noRegion")]
    [InlineData(@"Atyrau_Air\Onshore\S1\abc", "Atyrau", null)]
    [InlineData(@"Atyrau_Air\Onshore\1085", "Atyrau", "noSite")]
    public void Build__issues(string path, string? region, string? issueCode)
    {
        var plan = Build(E(path, region, "Onshore"));

        if (issueCode is null)
        {
            Assert.Empty(plan.Issues);
            Assert.Single(plan.Nodes, n => n.LevelCode == NaeTreeSchema.LevelSource);
        }
        else
        {
            Assert.Equal(issueCode, Assert.Single(plan.Issues).Code);
            Assert.DoesNotContain(plan.Nodes, n => n.LevelCode == NaeTreeSchema.LevelSource);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Build__deep_path_keeps_first_and_last_group_and_warns()
    {
        var plan = Build(E(@"Atyrau_Air\Onshore\S1\G1\G2\G3\1085", "Atyrau", "Onshore"));

        Assert.Equal("pathFlattened", Assert.Single(plan.Issues).Code);
        Assert.Contains(plan.Nodes, n => n.LevelCode == NaeTreeSchema.LevelEquipment && n.Display["en"] == "G3");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Build__truncated_listing_is_reported()
    {
        var plan = NaeAfTreeBuilder.Build(new NaeAfSnapshot([E(@"Atyrau_Air\Onshore\S1\1085", "Atyrau", "Onshore")], false), Root);
        Assert.Contains(plan.Issues, i => i.Code == "sourceIncomplete");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Build__codes_are_valid_unique_and_bounded()
    {
        var longName = new string('x', 90);
        var plan = Build(
            E($@"Atyrau_Air\Onshore\{longName}A\1085", "Atyrau", "Onshore"),
            E($@"Atyrau_Air\Onshore\{longName}B\1086", "Atyrau", "Onshore"),
            E(@"Atyrau_Air\Onshore\Площадка 1\1087", "Atyrau", "Onshore"),
            E(@"Atyrau_Air\Onshore\Площадка 2\1088", "Atyrau", "Onshore"));

        Assert.All(plan.Nodes, n => Assert.True(EcrCode.TryCreate(n.Code, out _), n.Code));
        Assert.Equal(plan.Nodes.Count, plan.Nodes.Select(n => n.Code).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(plan.Nodes, n => Assert.True(n.Code.Length <= 64));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Build__is_deterministic_and_parents_come_first()
    {
        var input = new[]
        {
            E(@"Atyrau_Air\Onshore\S1\1085", "Atyrau", "Onshore"),
            E(@"Atyrau_Air\Offshore\S2\1086", "Atyrau", "Offshore"),
        };
        var a = Build(input);
        var b = Build(input.Reverse().ToArray());

        Assert.Equal(a.Nodes.Select(n => n.Code), b.Nodes.Select(n => n.Code));
        var seen = new HashSet<string>();
        foreach (var n in a.Nodes)
        {
            Assert.True(n.ParentKey is null || seen.Contains(n.ParentKey), $"{n.Key} before its parent");
            seen.Add(n.Key);
        }
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Build__output_satisfies_the_hierarchy_placement_rules()
    {
        var spec = new[]
        {
            (NaeTreeSchema.LevelRegion, false), (NaeTreeSchema.LevelArea, true), (NaeTreeSchema.LevelSite, false),
            (NaeTreeSchema.LevelEquipment, true), (NaeTreeSchema.LevelSource, false),
        };
        var levels = spec.Select((s, i) => new RegistryLevelDef(
            1, i + 1, EcrCode.Create(s.Item1), new LocalizedText(new Dictionary<string, string> { ["en"] = s.Item1 }), s.Item2)).ToList();

        var plan = Build(
            E(@"Atyrau_Air\Offshore\S1\G\1085", "Atyrau", "Offshore"),
            E(@"Atyrau_Air\Onshore\S1\1086", "Atyrau", "Onshore"),
            E(@"Mangistau_Air\S3\1087", "Mangistau", null));

        var byKey = plan.Nodes.ToDictionary(n => n.Key);
        foreach (var node in plan.Nodes)
        {
            var parentLevel = node.ParentKey is null ? null : levels.Single(l => l.Code == byKey[node.ParentKey].LevelCode);
            Assert.Null(RegistryHierarchyRules.CheckPlacement(levels, parentLevel, levels.Single(l => l.Code == node.LevelCode)));
        }
    }
}
