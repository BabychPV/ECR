// tests/Ecr.Application.Tests/Permits/AirScopeMatcherTests.cs
using Ecr.Application.Permits;
using Ecr.Application.Permits.AirLimits;
using Ecr.Application.Permits.Dto;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// ✎ AIR-ON-AIR: the ONE mapping of Region / Area scope values to the NAE tree (coordinator 02.11): region by normalised English
/// name; area only where the tree region has areas, «… complex» dropped; any other area → region only.
/// </summary>
public sealed class AirScopeMatcherTests
{
    private static readonly IReadOnlyList<AirRegionCodeDto> Tree =
    [
        new("ATYRAU", "Atyrau", [new("OFFSHORE", "Offshore"), new("ONSHORE", null)]),
        new("MANGISTAU", null, [new(AirLimitsSchema.NoArea, null)]),
    ];

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("Atyrau", null, "ATYRAU", null)]
    [InlineData("  atyrau ", "Offshore", "ATYRAU", "OFFSHORE")]
    [InlineData("Atyrau", "Offshore complex", "ATYRAU", "OFFSHORE")]
    [InlineData("Atyrau", "Onshore complex", "ATYRAU", "ONSHORE")]
    [InlineData("Atyrau", "ONSHORE", "ATYRAU", "ONSHORE")]
    [InlineData("Atyrau", "Bautino base", "ATYRAU", null)]
    [InlineData("Mangistau", "Offshore", "MANGISTAU", null)]
    [InlineData("Mangistau", null, "MANGISTAU", null)]
    [InlineData("Kyzylorda", "Offshore", null, null)]
    [InlineData("", null, null, null)]
    public void Value_maps_to_the_NAE_tree_scope(string region, string? area, string? naeRegion, string? naeArea)
    {
        var match = AirScopeMatcher.Match(region, area, Tree);

        Assert.Equal(naeRegion, match.NaeRegion);
        Assert.Equal(naeArea, match.NaeArea);
        Assert.Equal(naeRegion is not null, match.InTree);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [InlineData("Offshore complex", "OFFSHORE")]
    [InlineData("Onshore", "ONSHORE")]
    [InlineData("Island A", null)]
    public void Area_code_is_found_in_any_tree_region_with_areas(string area, string? expected)
        => Assert.Equal(expected, AirScopeMatcher.AreaCode(area, Tree));

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Scope_columns_follow_the_column_settings_of_the_type()
    {
        static PermitTypeColumnDto Column(string code, string dataType, string? scope, string? registry)
            => new(code, new() { ["en"] = code }, dataType, null, code == "REGION", false, null, null, null, scope, registry, null,
                scope == "TypeOnly" ? ["A", "B"] : null, 1);

        PermitTypeTableDto Table(params PermitTypeColumnDto[] columns) => new(AirLimitsSchema.SourcesTable, new() { ["en"] = "S" }, 1, columns, []);

        Assert.Equal(
            new AirScopeColumn("REGION", AirLimitsSchema.KindRegistry, "LAND_REGION", true),
            AirScopeColumns.Of(Table(Column("REGION", "Lookup", "SharedRegistry", "LAND_REGION")), "REGION"));
        Assert.Equal(
            new AirScopeColumn("AREA", AirLimitsSchema.KindText, null, false),
            AirScopeColumns.Of(Table(Column("AREA", "String", null, null)), "AREA"));
        var list = AirScopeColumns.Of(Table(Column("AREA", "Lookup", "TypeOnly", null)), "AREA");
        Assert.Equal(AirLimitsSchema.KindList, list!.Kind);
        Assert.Equal(PermitHeaderFields.ListRegistryCode("AIR", AirLimitsSchema.SourcesTable, "AREA"), list.RegistryCode);
        Assert.Null(AirScopeColumns.Of(Table(), "AREA"));
    }
}
