// tests/Ecr.Infrastructure.Tests/Security/AccessProfileCacheTests.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Security;

/// <summary>
/// Кеш профілю доступу. Профіль будується **раз на сесію** (ФВ-6.10), але
/// відкликання прав діє **негайно** — через зміну `SecurityStamp`, яка дає
/// інший ключ кешу.
/// </summary>
public sealed class AccessProfileCacheTests
{
    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Профіль_будується_один_раз_на_сесію()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Зміна_SecurityStamp_дає_інший_ключ_кешу()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Відкликання_ролі_діє_негайно_а_не_після_закінчення_cookie()
        => Assert.Fail("not implemented");

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage3)]
    public void Профіль_симуляції_не_потрапляє_в_кеш()
        => Assert.Fail("not implemented");
}
