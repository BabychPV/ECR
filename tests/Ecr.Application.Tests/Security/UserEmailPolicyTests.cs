// tests/Ecr.Application.Tests/Security/UserEmailPolicyTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Security;
using Ecr.Domain.Entities.Notifications;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Security;

/// <summary>T1-04: пошта користувача проходить той самий суворий валідатор, що й адреси каналів.</summary>
public sealed class UserEmailPolicyTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData("not-an-email")]
    [InlineData("also bad@@x")]
    [InlineData("a b@example.test")]
    [InlineData("a@[127.0.0.1]")]
    public void Хибна_адреса_дає_422_з_ключем_і_адресою(string email)
    {
        var error = Assert.Throws<BusinessRuleException>(() => UserEmailPolicy.EnsureValid(email));

        Assert.Equal("ECR-USR-0422", error.ErrorCode);
        Assert.Equal(email, error.Details!["email"]);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("kovalenko@ncoc.kz")]
    [InlineData("  mary@corp.example ")]
    public void Порожня_і_коректна_адреси_проходять(string? email)
        => UserEmailPolicy.EnsureValid(email);

    // Фіксація поточної поведінки SmtpSettings.IsValidAddress щодо не-ASCII (T1-04).
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Кирилиця_в_локальній_частині_фіксована_поточною_поведінкою()
        => Assert.True(
            SmtpSettings.IsValidAddress("иван@example.com")
            && Record.Exception(() => UserEmailPolicy.EnsureValid("иван@example.com")) is null);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Юнікод_домен_фіксований_поточною_поведінкою()
        => Assert.True(
            SmtpSettings.IsValidAddress("ivan@пример.рф")
            && Record.Exception(() => UserEmailPolicy.EnsureValid("ivan@пример.рф")) is null);
}
