// tests/Ecr.Application.Tests/Permits/LookupTargetMatcherDenyTests.cs
using Ecr.Application.Permits.Groups;
using Ecr.Application.Security;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Permits;

/// <summary>
/// P2 (рішення людини 23.10: «Deny блокує і вибір запису»): <see cref="LookupTargetMatcher.Belongs"/> з профілем
/// відхиляє запис реєстру під Deny і на ПРЯМОМУ збігу цілі; без профілю (системні шляхи) - як і раніше.
/// Мутація: повернути «збіг реєстру - true» без перевірки Deny - тести з Deny червоніють.
/// </summary>
public sealed class LookupTargetMatcherDenyTests
{
    private const int Target = 55;
    private const int Other = 59;

    private static AccessProfile Profile(params int[] denied) => new()
    {
        CacheKey = "k", UserId = 1, SecurityStamp = "s",
        Permissions = new HashSet<string>(), Grants = new Dictionary<string, Ecr.Domain.Enums.GrantLevel>(),
        Denies = new HashSet<string>(denied.Select(id => "Registry:" + id)), RoleIds = new HashSet<int>(),
    };

    [Fact]
    public void Пряма_ціль_під_Deny_не_належить()
        => Assert.False(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), Profile(Target), Target, Target));

    [Fact]
    public void Пряма_ціль_під_Deny_не_належить_і_без_резолвера()
        => Assert.False(LookupTargetMatcher.Belongs(null, Profile(Target), Target, Target));

    [Fact]
    public void Пряма_ціль_без_Deny_належить_і_не_кличе_резолвер()
    {
        var groups = new FakePermitGroupResolver { ThrowOnUse = true };

        Assert.True(LookupTargetMatcher.Belongs(groups, Profile(), Target, Target));
        Assert.Equal(0, groups.Calls);
    }

    [Fact]
    public void Пряма_ціль_Deny_на_інший_довідник_належить()
        => Assert.True(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), Profile(Other), Target, Target));

    [Fact]
    public void Системний_шлях_без_профілю_Deny_не_застосовує()
        => Assert.True(LookupTargetMatcher.Belongs(new FakePermitGroupResolver(), null, Target, Target));

    [Fact]
    public void Група_реєстр_члена_під_Deny_не_належить()
    {
        var groups = new FakePermitGroupResolver();
        groups.WithGroup(Target, [Other]);

        Assert.True(LookupTargetMatcher.Belongs(groups, Profile(), Target, Other));
        Assert.False(LookupTargetMatcher.Belongs(groups, Profile(Other), Target, Other));
    }
}
