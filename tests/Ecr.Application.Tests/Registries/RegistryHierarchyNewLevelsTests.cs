// tests/Ecr.Application.Tests/Registries/RegistryHierarchyNewLevelsTests.cs
using Ecr.Application.Registries.Hierarchy;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>
/// N1 defect found by N2: declaring the first levels of a hierarchy saves several levels with Id 0 at once, and
/// <c>FindViolations</c> indexed levels by Id (duplicate key 0 → 500). Mutation proof: remove the <c>Id != 0</c> filter → this fails.
/// </summary>
public sealed class RegistryHierarchyNewLevelsTests
{
    private static readonly string[] Codes = ["A", "B", "C"];

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void FindViolations__several_unsaved_levels_do_not_collide()
    {
        var levels = Codes
            .Select((c, i) => new RegistryLevelDef(
                1, i + 1, EcrCode.Create(c), new LocalizedText(new Dictionary<string, string> { ["en"] = c }), false))
            .ToList();

        var result = RegistryHierarchyRules.FindViolations(levels, [], new Dictionary<long, HierarchyNode>());

        Assert.Empty(result);
    }
}
