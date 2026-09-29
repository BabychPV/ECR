// tests/Ecr.Application.Tests/Security/UserEmailMaskTests.cs
using Ecr.Application.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>S20: маска адреси в журналі — перші два символи й домен, не більше.</summary>
public sealed class UserEmailMaskTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S20")]
    [InlineData("john.doe@kpo.example", "jo***@kpo.example")]
    [InlineData("  mary@corp.example ", "ma***@corp.example")]
    [InlineData("a@x.example", "a***@x.example")]
    [InlineData("no-at-sign", "no***")]
    public void Маска_лишає_два_символи_й_домен(string email, string expected)
        => Assert.Equal(expected, SetUserEmailHandler.MaskEmail(email));

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait("Finding", "S20")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Порожня_адреса_без_маски(string? email)
        => Assert.Null(SetUserEmailHandler.MaskEmail(email));
}
