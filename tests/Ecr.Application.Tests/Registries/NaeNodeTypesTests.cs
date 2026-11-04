// tests/Ecr.Application.Tests/Registries/NaeNodeTypesTests.cs
using Ecr.Application.Registries.Hierarchy;
using Ecr.Application.Registries.Nae;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// NAE node types (human 03.11, quiz), pure parts: SITE inside SITE only for a self-nesting level (Q2), field-to-level binding,
/// the template map and the AF load by template (Air_Area inside Air_Area, Equipment under Air_Unit), derived region/area codes.
/// </summary>
/// <remarks>
/// Mutation proofs (local, not committed): drop the self-nesting branch of <c>RegistryHierarchyRules.CheckPlacement</c> → the
/// SITE/SITE row fails; let it accept any equal code regardless of the flag → the "flag off" row fails; make
/// <c>RegistryFieldDef.AppliesToLevel</c> ignore the level → the binding rows fail; map <c>Air_Area</c> to EQUIP → the template
/// plan fails.
/// </remarks>
public sealed class NaeNodeTypesTests
{
    private const string Root = @"\\AF\ECR\ECR_01_Air";

    private static readonly string[] PlanLevels = ["REGION", "AREA", "SITE", "SITE", "EQUIP", "SRC", "SRC", "REGION", "SITE", "SRC"];

    private static LocalizedText Name(string v) => new(new Dictionary<string, string> { ["en"] = v });

    private static List<RegistryLevelDef> Levels(bool siteNests)
    {
        var spec = new[] { ("REGION", false), ("AREA", true), ("SITE", false), ("EQUIP", true), ("SRC", false) };
        var list = new List<RegistryLevelDef>();
        for (var i = 0; i < spec.Length; i++)
        {
            var level = new RegistryLevelDef(1, i + 1, EcrCode.Create(spec[i].Item1), Name(spec[i].Item1), spec[i].Item2);
            level.AllowSelfNesting(siteNests && spec[i].Item1 == "SITE");
            list.Add(level);
        }

        return list;
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("SITE", "SITE", true, null)]
    [InlineData("SITE", "SITE", false, "err.ECR-REG-0422.levelOrder")]
    [InlineData("EQUIP", "EQUIP", true, "err.ECR-REG-0422.levelOrder")]
    [InlineData("SRC", "SRC", true, "err.ECR-REG-0422.levelOrder")]
    [InlineData("REGION", "REGION", true, "err.ECR-REG-0422.levelOrder")]
    [InlineData("EQUIP", "SITE", true, "err.ECR-REG-0422.levelOrder")]
    [InlineData("SITE", "EQUIP", true, null)]
    public void CheckPlacement__only_a_self_nesting_level_nests_and_only_in_itself(string parent, string level, bool siteNests, string? expected)
    {
        var levels = Levels(siteNests);

        Assert.Equal(
            expected,
            RegistryHierarchyRules.CheckPlacement(levels, levels.Single(l => l.Code == parent), levels.Single(l => l.Code == level)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void FindViolations__a_site_chain_is_valid_when_site_nests_and_invalid_otherwise()
    {
        var on = Levels(siteNests: true).Select((l, i) => WithId(l, i + 1)).ToList();
        var off = Levels(siteNests: false).Select((l, i) => WithId(l, i + 1)).ToList();
        var nodes = new Dictionary<long, HierarchyNode>
        {
            [1] = new(1, null, 1),
            [2] = new(2, 1, 3),
            [3] = new(3, 2, 3),
            [4] = new(4, 3, 3),
            [5] = new(5, 4, 5),
        };

        Assert.Empty(RegistryHierarchyRules.FindViolations(on, nodes.Values, nodes));
        Assert.Equal([3L, 4L], RegistryHierarchyRules.FindViolations(off, nodes.Values, nodes).Select(v => v.Id).Order().ToArray());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(null, null, true, true)]
    [InlineData(null, 5, true, true)]
    [InlineData(5, null, true, false)]
    [InlineData(5, 5, true, true)]
    [InlineData(5, 3, false, false)]
    public void Field_level__a_bound_field_applies_and_is_required_only_on_its_level(int? fieldLevel, int? entryLevel, bool applies, bool required)
    {
        var field = new RegistryFieldDef(1, EcrCode.Create("NaeCode"), Name("NAE code"), CellDataType.String, 1);
        field.Update(field.NameL10n, 1, isRequired: true);
        field.BindToLevel(fieldLevel);

        Assert.Equal((applies, required), (field.AppliesToLevel(entryLevel), field.IsRequiredFor(entryLevel)));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("Air_Region", "REGION")]
    [InlineData("air_onshoreoffshore", "AREA")]
    [InlineData("Air_Area", "SITE")]
    [InlineData("Air_Unit", "EQUIP")]
    [InlineData(" Equipment ", "SRC")]
    [InlineData("Element", null)]
    [InlineData(null, null)]
    public void Template_map__af_template_names_give_the_node_type(string? template, string? level)
        => Assert.Equal(level, NaeTreeSchema.LevelOfAfTemplate(template));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(null, "REGION", true)]
    [InlineData(null, "SITE", false)]
    [InlineData("REGION", "SITE", true)]
    [InlineData("REGION", "SRC", true)]
    [InlineData("AREA", "EQUIP", true)]
    [InlineData("AREA", "SRC", true)]
    [InlineData("REGION", "EQUIP", true)]
    [InlineData("SITE", "SITE", true)]
    [InlineData("EQUIP", "EQUIP", false)]
    [InlineData("AREA", "AREA", false)]
    [InlineData("SITE", "SRC", true)]
    [InlineData("EQUIP", "SRC", true)]
    public void IsValidPlacement__mirrors_the_registry_rules_with_site_nesting(string? parent, string level, bool valid)
        => Assert.Equal(valid, NaeTreeSchema.IsValidPlacement(parent, level));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("R_ATYRAU", null, "ATYRAU", "-")]
    [InlineData("R_ATYRAU", "A_ATYRAU_OFFSHORE", "ATYRAU", "OFFSHORE")]
    [InlineData("MANGISTAU", null, "MANGISTAU", "-")]
    [InlineData("r_atyrau", "onshore", "ATYRAU", "ONSHORE")]
    public void Derived_codes__region_and_area_come_from_the_ancestor_codes(string regionCode, string? areaCode, string region, string area)
    {
        var derived = NaeTreeSchema.DerivedRegion(regionCode);

        Assert.Equal((region, area), (derived, NaeTreeSchema.DerivedArea(areaCode, derived)));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Af_by_template__site_inside_site_and_equipment_under_unit()
    {
        var plan = NaeAfTreeBuilder.Build(
            new NaeAfSnapshot(
            [
                El(@"Atyrau", "Air_Region"),
                El(@"Atyrau\Offshore", "Air_OnShoreOffShore"),
                El(@"Atyrau\Offshore\Island_A", "Air_Area", "Island A"),
                El(@"Atyrau\Offshore\Island_A\Block_2", "Air_Area", "Block 2"),
                El(@"Atyrau\Offshore\Island_A\Block_2\Folder", null),
                El(@"Atyrau\Offshore\Island_A\Block_2\Folder\Gen_1", "Air_Unit", "Generator 1"),
                El(@"Atyrau\Offshore\Island_A\Block_2\Folder\Gen_1\1085", "Equipment", "Stack 1085"),
                El(@"Atyrau\Offshore\Island_A\36_pod", "Equipment"),
                El(@"Mangistau", "Air_Region"),
                El(@"Mangistau\ZPK_1", "Air_Area"),
                El(@"Mangistau\ZPK_1\1085", "Equipment"),
            ],
                IsComplete: true),
            Root);

        Assert.Empty(plan.Issues);
        var byCode = plan.Nodes.ToDictionary(n => n.Code);
        Assert.Equal(PlanLevels.Order(), plan.Nodes.Select(n => n.LevelCode).Order());

        var island = byCode["S_ATYRAU_OFFSHORE_ISLAND_A"];
        var block = byCode["S_ATYRAU_OFFSHORE_ISLAND_A_BLOCK_2"];
        Assert.Equal(island.Key, block.ParentKey);
        Assert.Equal("Block 2", block.Display["en"]);

        var unit = byCode["G_ATYRAU_OFFSHORE_ISLAND_A_BLOCK_2_GEN_1"];
        Assert.Equal(block.Key, unit.ParentKey);
        var stack = byCode["N_ATYRAU_OFFSHORE_1085"];
        Assert.Equal(unit.Key, stack.ParentKey);
        Assert.Equal(("1085", "ATYRAU", "OFFSHORE"), (stack.Fields["NaeCode"], stack.Fields["RegionCode"], stack.Fields["AreaCode"]));
        Assert.Equal("0036_pod", byCode["N_ATYRAU_OFFSHORE_0036_POD"].Fields["NaeCode"]);

        // A region without On/Offshore: area "-", the same NAE number as in Atyrau is a different source.
        var zpk = byCode["N_MANGISTAU_NA_1085"];
        Assert.Equal(("MANGISTAU", "-"), (zpk.Fields["RegionCode"], zpk.Fields["AreaCode"]));

        // Parents before children, also inside one level (the applier writes level by level, in this order).
        var position = plan.Nodes.Select((n, i) => (n.Key, i)).ToDictionary(x => x.Key, x => x.i);
        Assert.All(plan.Nodes.Where(n => n.ParentKey is not null), n => Assert.True(position[n.ParentKey!] < position[n.Key], n.Code));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Af_by_template__wrong_places_and_unknown_templates_are_skipped_with_their_subtree()
    {
        var plan = NaeAfTreeBuilder.Build(
            new NaeAfSnapshot(
            [
                El(@"Atyrau", "Air_Region"),
                El(@"Atyrau\1001", "Equipment"),                // SRC directly under REGION is valid since SITE is optional (04.11)
                El(@"Atyrau\Site", "Air_Area"),
                El(@"Atyrau\Site\Odd", "Something_Else"),       // unknown template
                El(@"Atyrau\Site\Odd\1002", "Equipment"),       // below a skipped element
                El(@"Atyrau\Site\Unit", "Air_Unit"),
                El(@"Atyrau\Site\Unit\Unit2", "Air_Unit"),      // EQUIP does not nest
                El(@"Atyrau\Site\1003", "Equipment"),
            ],
                IsComplete: true),
            Root);

        Assert.Equal(
            ["levelOrder", "parentSkipped", "unknownTemplate"],
            plan.Issues.Select(i => i.Code).Order().ToArray());
        Assert.Equal(["N_ATYRAU_NA_1001", "N_ATYRAU_NA_1003"], plan.Nodes.Where(n => n.LevelCode == "SRC").Select(n => n.Code).Order().ToArray());
    }

    private static NaeAfElement El(string relative, string? template, string? en = null)
        => new($@"{Root}\{relative}", relative.Split('\\')[^1], en, null, null, null, null, template);

    private static RegistryLevelDef WithId(RegistryLevelDef level, int id)
    {
        typeof(RegistryLevelDef).BaseType!.GetProperty("Id")!.SetValue(level, id);
        return level;
    }
}
