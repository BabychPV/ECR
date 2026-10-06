// tests/Ecr.Api.Tests/ValidationFindingDisplayCodeTests.cs

using Ecr.Api.Controllers;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// A3: сирий validate віддавав <c>ruleCode = REL-CHK_TOT_v5</c> (службовий префікс і суфікс версії клона).
/// Окреме поле <c>displayCode</c> несе код для людини; <c>ruleCode</c> лишається адресою знахідки
/// (наскрізне відображення в обох відповідях — <c>ValidationFindingTableTests</c>).
/// </summary>
public sealed class ValidationFindingDisplayCodeTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait("Requirement", "A3")]
    [InlineData("REL-CHK_TOT_v5", "CHK_TOT")]
    [InlineData("REL-CHK_TOT", "CHK_TOT")]
    [InlineData("REL-CHK_v12", "CHK")]
    [InlineData("REL-MASS_v2_TOTAL", "MASS_v2_TOTAL")]
    [InlineData("REL-CHK_vX", "CHK_vX")]
    [InlineData("CAP_v2", "CAP_v2")]
    [InlineData("ECR-VAL-RULE", "ECR-VAL-RULE")]
    [InlineData("REL-", "REL-")]
    [InlineData("REL-_v5", "REL-_v5")]
    public void Код_для_показу_знімає_REL_і_суфікс_версії(string ruleCode, string expected)
        => Assert.Equal(expected, ValidationFindingDto.DisplayCodeOf(ruleCode));
}
