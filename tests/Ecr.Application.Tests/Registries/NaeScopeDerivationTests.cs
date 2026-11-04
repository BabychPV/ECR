// tests/Ecr.Application.Tests/Registries/NaeScopeDerivationTests.cs
using Ecr.Application.Registries.Nae;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Registries;

/// <summary>AN-158: a code derived from a technical code never comes out as a run of «_».</summary>
public sealed class NaeScopeDerivationTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("A_ATYRAU_OFFSHORE", "ATYRAU", "OFFSHORE")]
    [InlineData("A_ATYRAU_3F2A91C0", "ATYRAU", "3F2A91C0")]
    [InlineData("A_ATYRAU_____", "ATYRAU", "")]
    [InlineData("_____", "ATYRAU", "")]
    public void DerivedArea__an_all_underscore_result_is_empty(string technical, string region, string expected)
        => Assert.Equal(expected, NaeTreeSchema.DerivedArea(technical, region));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("R_ATYRAU", "ATYRAU")]
    [InlineData("R_____", "")]
    [InlineData("_____", "")]
    public void DerivedRegion__an_all_underscore_result_is_empty(string technical, string expected)
        => Assert.Equal(expected, NaeTreeSchema.DerivedRegion(technical));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void DerivedArea__no_area_node_is_still_the_no_area_marker()
        => Assert.Equal(NaeAfTreeBuilder.NoArea, NaeTreeSchema.DerivedArea(null, "ATYRAU"));
}
