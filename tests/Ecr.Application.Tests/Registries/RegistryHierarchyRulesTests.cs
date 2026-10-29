// tests/Ecr.Application.Tests/Registries/RegistryHierarchyRulesTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Registries.Hierarchy;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>NAE plan N1: placement rules of hierarchical registries (pure functions).</summary>
/// <remarks>
/// Mutation proof: make <c>CheckPlacement</c> return <c>null</c> → the order and skip cases fail; make <c>WouldCycle</c>
/// return <c>false</c> → the cycle cases fail.
/// </remarks>
public sealed class RegistryHierarchyRulesTests
{
    private static LocalizedText Name(string v) => new(new Dictionary<string, string> { ["en"] = v });

    // REGION(1), AREA(2, optional), SITE(3), EQUIP(4, optional), SRC(5)
    private static List<RegistryLevelDef> Levels()
    {
        var spec = new[] { ("REGION", false), ("AREA", true), ("SITE", false), ("EQUIP", true), ("SRC", false) };
        var list = new List<RegistryLevelDef>();
        for (var i = 0; i < spec.Length; i++)
        {
            list.Add(new RegistryLevelDef(1, i + 1, EcrCode.Create(spec[i].Item1), Name(spec[i].Item1), spec[i].Item2));
        }

        return list;
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(null, "REGION", null)]
    [InlineData(null, "AREA", "err.ECR-REG-0422.levelSkipsRequired")]
    [InlineData(null, "SITE", "err.ECR-REG-0422.levelSkipsRequired")]
    [InlineData("REGION", "AREA", null)]
    [InlineData("REGION", "SITE", null)]
    [InlineData("REGION", "SRC", "err.ECR-REG-0422.levelSkipsRequired")]
    [InlineData("SITE", "SITE", "err.ECR-REG-0422.levelOrder")]
    [InlineData("SITE", "AREA", "err.ECR-REG-0422.levelOrder")]
    [InlineData("SITE", "EQUIP", null)]
    [InlineData("SITE", "SRC", null)]
    [InlineData("EQUIP", "SRC", null)]
    [InlineData("SRC", "SRC", "err.ECR-REG-0422.levelOrder")]
    public void CheckPlacement__order_and_skipped_levels(string? parent, string level, string? expected)
    {
        var levels = Levels();
        var parentLevel = parent is null ? null : levels.Single(l => l.Code == parent);

        Assert.Equal(expected, RegistryHierarchyRules.CheckPlacement(levels, parentLevel, levels.Single(l => l.Code == level)));
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(1, 1, true)]   // itself
    [InlineData(1, 3, true)]   // own grandchild: 3 -> 2 -> 1
    [InlineData(1, 2, true)]   // own child
    [InlineData(3, 1, false)]  // moving a leaf up
    [InlineData(1, 9, false)]  // unrelated root
    public void WouldCycle__walks_up_from_the_new_parent(long moved, long newParent, bool expected)
    {
        var parents = new Dictionary<long, long?> { [1] = null, [2] = 1, [3] = 2, [9] = null };

        Assert.Equal(expected, RegistryHierarchyRules.WouldCycle(moved, newParent, id => parents[id]));
        Assert.False(RegistryHierarchyRules.WouldCycle(moved, null, id => parents[id]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void WouldCycle__an_already_cyclic_chain_terminates_and_counts_as_a_cycle()
    {
        var parents = new Dictionary<long, long?> { [5] = 6, [6] = 5, [7] = null };

        Assert.True(RegistryHierarchyRules.WouldCycle(7, 5, id => parents[id]));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void FindViolations__reports_only_nodes_in_an_invalid_place()
    {
        var levels = Levels();
        int Id(string code) => levels.Single(l => l.Code == code).Id;
        // Entity ids are 0 until saved: give the levels distinct ids through the node map instead.
        for (var i = 0; i < levels.Count; i++)
        {
            typeof(RegistryLevelDef).BaseType!.GetProperty("Id")!.SetValue(levels[i], i + 1);
        }

        var all = new Dictionary<long, HierarchyNode>
        {
            [1] = new(1, null, Id("REGION")),
            [2] = new(2, 1, Id("SITE")),      // fine: AREA optional
            [3] = new(3, 2, Id("SRC")),       // fine: EQUIP optional
            [4] = new(4, 1, Id("SRC")),       // SITE (required) skipped
            [5] = new(5, 2, Id("SITE")),      // not deeper than the parent
            [6] = new(6, 1, null),            // no level
        };

        var violations = RegistryHierarchyRules.FindViolations(levels, all.Values, all);

        Assert.Equal(
            new (long, string)[]
            {
                (4, "err.ECR-REG-0422.levelSkipsRequired"),
                (5, "err.ECR-REG-0422.levelOrder"),
                (6, "err.ECR-REG-0422.levelRequired"),
            },
            violations.OrderBy(v => v.Id).ToArray());
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData(0)]
    [InlineData(7)]
    public void EnsureLevelListValid__count_must_be_1_to_6(int count)
    {
        var list = Enumerable.Range(1, count).Select(i => new RegistryLevelInput($"L{i}", Name("x"), false)).ToList();

        var ex = Assert.Throws<BusinessRuleException>(() => RegistryHierarchyRules.EnsureLevelListValid(list));
        Assert.Equal("ECR-REG-0422", ex.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void EnsureLevelListValid__duplicate_codes_and_missing_english_name_are_refused_valid_list_passes()
    {
        Assert.Throws<BusinessRuleException>(
            () => RegistryHierarchyRules.EnsureLevelListValid([new("A", Name("a"), false), new("a", Name("b"), false)]));
        Assert.Throws<BusinessRuleException>(
            () => RegistryHierarchyRules.EnsureLevelListValid(
                [new("A", new LocalizedText(new Dictionary<string, string> { ["ru"] = "а" }), false)]));
        Assert.Throws<BusinessRuleException>(
            () => RegistryHierarchyRules.EnsureLevelListValid([new("A", Name(" "), false)]));

        RegistryHierarchyRules.EnsureLevelListValid(
            [new("REGION", Name("Region"), false), new("AREA", Name("Area"), true)]);
    }
}
