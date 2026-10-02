// tests/Ecr.Api.Tests/UnitConversionDimensionErrorTests.cs
using System.Text.Json;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// ФВ-16.5 (AN-13): порушення <c>CK_Conv_SameDimension</c> віддається клієнтові як 422
/// <c>ECR-UOM-4221</c>, а не голим 500.
/// </summary>
public sealed class UnitConversionDimensionErrorTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.5")]
    public async Task Конверсія_між_різними_розмірностями_віддається_як_422_ECR_UOM_4221()
    {
        var body = await ProblemReservedMembersTests.ProblemTextAsync(new DbUpdateException(
            "save failed",
            new InvalidOperationException(
                "The INSERT statement conflicted with the CHECK constraint \"CK_Conv_SameDimension\".")));

        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        Assert.Equal("ECR-UOM-4221", problem.GetProperty("errorCode").GetString());
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-16.5")]
    public async Task Інша_відмова_збереження_не_маскується_під_4221()
    {
        var body = await ProblemReservedMembersTests.ProblemTextAsync(new DbUpdateException(
            "save failed", new InvalidOperationException("some other constraint")));

        var problem = JsonDocument.Parse(body).RootElement;

        Assert.Equal(500, problem.GetProperty("status").GetInt32());
        Assert.NotEqual("ECR-UOM-4221", problem.GetProperty("errorCode").GetString());
    }
}
