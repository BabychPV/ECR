// tests/Ecr.Application.Tests/Preferences/UserPreferenceRulesTests.cs
using Ecr.Application.Preferences;
using Xunit;

namespace Ecr.Application.Tests.Preferences;

/// <summary>
/// Білий список ключів налаштувань (<c>BE-20</c>): ключ, якого в ньому немає,
/// сервер відкидає з <c>422</c>, і клієнт мовчки втрачає вибір людини між
/// пристроями — локально все виглядає справним.
/// </summary>
public sealed class UserPreferenceRulesTests
{
    [Theory]
    [InlineData("theme")]
    [InlineData("density")]
    [InlineData("language")]
    [InlineData("navbarCollapsed")]
    [InlineData("grid.columnWidths.42")]
    public void Ключ_з_білого_списку_допустимий(string key)
        => Assert.True(UserPreferenceRules.IsKeyAllowed(key));

    [Theory]
    [InlineData("navbar")]
    [InlineData("navbarCollapsed.x")]
    [InlineData("unknownKey")]
    [InlineData("")]
    public void Ключ_поза_білим_списком_недопустимий(string key)
        => Assert.False(UserPreferenceRules.IsKeyAllowed(key));
}
